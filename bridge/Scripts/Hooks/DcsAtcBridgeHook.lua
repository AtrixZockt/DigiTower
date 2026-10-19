-- DCS-ATC bridge hook.
--
-- Install: Saved Games\<DCS instance>\Scripts\Hooks\DcsAtcBridgeHook.lua
--          Saved Games\<DCS instance>\Scripts\DcsAtc\DcsAtcMission.lua
--
-- Listens on 127.0.0.1 for the ATC app (newline-delimited JSON, see docs/bridge-protocol.md).
-- When a mission starts, it injects DcsAtcMission.lua into the mission scripting environment,
-- polls it a few times per second and forwards the app's commands to it.
--
-- Running code in the mission needs this in Saved Games\<DCS instance>\Config\autoexec.cfg:
--   net.allow_unsafe_api = { "userhooks" }
--   net.allow_dostring_in = { "mission" }

local DcsAtcBridge = {}

local Sim = Sim or DCS
local dcslog = log

local CONFIG = {
  host = '127.0.0.1',
  port = 42700,
  pollInterval = 0.2,   -- seconds between polls of events and menu selections
  unitInterval = 0.5,   -- seconds between unit snapshots
  maxOutBuffer = 8 * 1024 * 1024,
  manageAi = true,      -- false: the app may not hold or release AI on this server (AI departures are left to DCS)
}

local scriptDir = lfs.writedir() .. 'Scripts/DcsAtc/'

local function logInfo(msg) dcslog.write('DCS-ATC', dcslog.INFO, msg) end
local function logError(msg) dcslog.write('DCS-ATC', dcslog.ERROR, msg) end

-- Settings: Scripts/DcsAtc/DcsAtcConfig.lua (written by bridge/install.ps1) returning e.g. { port = 42701, manageAi = false }.
-- A broken file is reported (in dcs.log and to the app) and the defaults are used.
local configError
do
  local chunk, err = loadfile(scriptDir .. 'DcsAtcConfig.lua')
  if chunk then
    local ok, overrides = pcall(chunk)
    if ok and type(overrides) == 'table' then
      for k, v in pairs(overrides) do CONFIG[k] = v end
    else
      configError = 'DcsAtcConfig.lua ignored, it must return a table { ... }: ' .. tostring(overrides)
    end
  elseif err and not tostring(err):find('cannot open', 1, true) then
    configError = 'DcsAtcConfig.lua ignored: ' .. tostring(err)
  end
  if configError then logError(configError) end
  if CONFIG.manageAi == false then logInfo('AI handling turned off (manageAi = false in DcsAtcConfig.lua)') end
end

package.path = package.path .. ';.\\LuaSocket\\?.lua'
package.cpath = package.cpath .. ';.\\LuaSocket\\?.dll'
local socket = require('socket')

local state = {
  server = nil,
  client = nil,
  inBuffer = '',
  outBuffer = '',
  missionRunning = false,
  missionInfo = nil,
  lastError = nil,      -- reported in `hello`, because the app may connect after the error happened
  lastPoll = 0,
  lastUnits = 0,
}

---------------------------------------------------------------------------------------------------
-- Running code in the mission scripting environment
---------------------------------------------------------------------------------------------------

--- Runs `code` in the mission scripting environment and returns its result as a string.
-- Returns nil and an error message on failure.
--
-- DCS bug (since 2.9.18, still in 2.9.30): a_do_script() prepends a nil to the values it passes
-- back and drops the last one, so `return a_do_script("return 42")` yields nil. The code therefore
-- runs inside a function whose result is returned twice; the second value is the result whether
-- or not the bug is present. https://forum.dcs.world/topic/376809-a_do_script-return-value-pass-thru-mangled-since-dcs-291812722/
local function runInMission(code)
  local inner = 'local __dcsatc = function()\n' .. code .. '\nend\nlocal __r = __dcsatc()\nreturn __r, __r'
  local wrapped = 'local a, b = a_do_script(' .. string.format('%q', inner) .. ')\nif b ~= nil then return b end\nreturn a'
  local ok, result, success = pcall(net.dostring_in, 'mission', wrapped)
  if not ok then return nil, tostring(result) end
  if success == false then return nil, tostring(result) end
  return result
end

--- Converts a decoded JSON value into a Lua literal, so commands reach the mission as data, never as code.
local function toLiteral(v, depth)
  depth = depth or 0
  if depth > 32 then error('command nested too deep') end
  local t = type(v)
  if t == 'string' then
    return string.format('%q', v)
  elseif t == 'number' then
    if v ~= v or v == math.huge or v == -math.huge then return '0' end
    return string.format('%.17g', v)
  elseif t == 'boolean' then
    return tostring(v)
  elseif t == 'table' then
    local parts = {}
    for k, val in pairs(v) do
      local kt = type(k)
      if kt == 'string' or kt == 'number' then
        parts[#parts + 1] = '[' .. toLiteral(k, depth + 1) .. ']=' .. toLiteral(val, depth + 1)
      end
    end
    return '{' .. table.concat(parts, ',') .. '}'
  end
  return 'nil'
end

---------------------------------------------------------------------------------------------------
-- Socket handling
---------------------------------------------------------------------------------------------------

local function disconnect(reason)
  if state.client then
    logInfo('ATC app disconnected: ' .. tostring(reason))
    pcall(function() state.client:close() end)
    -- AI held at its parking spots by the app must not stay there.
    if state.missionRunning then
      local released = runInMission('if ATCB and ATCB.releaseHeld then return tostring(ATCB.releaseHeld()) end return "0"')
      if released and released ~= '0' then logInfo('held AI groups released: ' .. tostring(released)) end
    end
  end
  state.client = nil
  state.inBuffer = ''
  state.outBuffer = ''
end

local function queueLine(line)
  if not state.client then return end
  state.outBuffer = state.outBuffer .. line .. '\n'
  if #state.outBuffer > CONFIG.maxOutBuffer then
    disconnect('app is not reading fast enough')
  end
end

local function send(msg)
  queueLine(net.lua2json(msg))
end

local function flush()
  if not state.client or state.outBuffer == '' then return end
  local sent, err, lastSent = state.client:send(state.outBuffer)
  if sent then
    state.outBuffer = ''
  elseif err == 'timeout' then
    state.outBuffer = state.outBuffer:sub((lastSent or 0) + 1)
  else
    disconnect(err)
  end
end

local function hello()
  send({
    type = 'hello',
    protocol = 1,
    mission = Sim.getMissionName(),
    missionRunning = state.missionRunning,
    info = state.missionInfo,
    multiplayer = Sim.isMultiplayer(),
    manageAi = CONFIG.manageAi ~= false,
    error = state.lastError or configError,
  })
end

local function listen()
  local server, err = socket.bind(CONFIG.host, CONFIG.port)
  if not server then
    logError(string.format('cannot listen on %s:%d: %s', CONFIG.host, CONFIG.port, tostring(err)))
    return
  end
  server:settimeout(0)
  state.server = server
  logInfo(string.format('listening on %s:%d', CONFIG.host, CONFIG.port))
end

local function accept()
  if not state.server then return end
  local client = state.server:accept()
  if not client then return end
  if state.client then disconnect('replaced by a new connection') end
  client:settimeout(0)
  client:setoption('tcp-nodelay', true)
  state.client = client
  logInfo('ATC app connected')
  hello()
end

local function reply(cmd, ok, err)
  if cmd.req then
    send({ type = 'reply', req = cmd.req, cmd = cmd.cmd, ok = ok, error = err })
  end
end

local function handleLine(line)
  local ok, cmd = pcall(net.json2lua, line)
  if not ok or type(cmd) ~= 'table' or type(cmd.cmd) ~= 'string' then
    send({ type = 'error', error = 'invalid command line' })
    return
  end
  if cmd.cmd == 'hello' then
    hello()
    return
  end
  if CONFIG.manageAi == false and (cmd.cmd == 'group_hold' or cmd.cmd == 'group_release') then
    reply(cmd, false, 'AI handling is turned off on this server (manageAi = false in DcsAtcConfig.lua)')
    return
  end
  if not state.missionRunning then
    reply(cmd, false, 'no mission running')
    return
  end
  local _, err = runInMission('ATCB.command(' .. toLiteral(cmd) .. ') return ""')
  if err then reply(cmd, false, err) end
end

local function receive()
  while state.client do
    local line, err, partial = state.client:receive('*l')
    if line then
      local full = state.inBuffer .. line
      state.inBuffer = ''
      handleLine(full)
    else
      if partial and partial ~= '' then state.inBuffer = state.inBuffer .. partial end
      if err == 'closed' then disconnect('closed') end
      return
    end
  end
end

local function poll()
  if not state.missionRunning then return end
  local now = Sim.getRealTime()
  if now - state.lastPoll < CONFIG.pollInterval then return end
  state.lastPoll = now
  local withUnits = now - state.lastUnits >= CONFIG.unitInterval
  if withUnits then state.lastUnits = now end
  local result, err = runInMission('return ATCB.poll(' .. tostring(withUnits) .. ')')
  if err then
    logError('poll failed: ' .. err)
    send({ type = 'error', error = 'poll failed: ' .. err })
  elseif result and result ~= '' then
    queueLine(result)
  end
end

---------------------------------------------------------------------------------------------------
-- Mission lifecycle
---------------------------------------------------------------------------------------------------

local function injectMission()
  local file, openErr = io.open(scriptDir .. 'DcsAtcMission.lua', 'r')
  if not file then
    logError('mission script not found: ' .. tostring(openErr))
    return
  end
  local code = file:read('*a')
  file:close()
  local info, err = runInMission(code .. '\nreturn ATCB.start()')
  if err or not info or info == '' then
    state.lastError = 'mission injection failed: ' .. (err or 'no reply from the mission (see dcs.log for SCRIPTING errors)')
    logError(state.lastError)
    send({ type = 'error', error = state.lastError })
    return
  end
  local okDecode, decoded = pcall(net.json2lua, info)
  state.missionInfo = okDecode and decoded or nil
  state.missionRunning = true
  state.lastError = nil
  state.lastPoll, state.lastUnits = 0, 0
  logInfo('mission side started: ' .. tostring(Sim.getMissionName()))
  send({ type = 'mission_start', mission = Sim.getMissionName(), info = state.missionInfo })
end

local function guarded(fn)
  return function(...)
    local ok, err = pcall(fn, ...)
    if not ok then logError(tostring(err)) end
  end
end

function DcsAtcBridge.onSimulationStart()
  if not Sim.isServer() then return end
  injectMission()
end

function DcsAtcBridge.onSimulationStop()
  if state.missionRunning then
    state.missionRunning = false
    state.missionInfo = nil
    send({ type = 'mission_stop' })
    flush()
  end
end

function DcsAtcBridge.onSimulationFrame()
  accept()
  if not state.client then return end
  receive()
  poll()
  flush()
end

local callbacks = {}
for name, fn in pairs(DcsAtcBridge) do
  callbacks[name] = guarded(fn)
end

listen()
Sim.setUserCallbacks(callbacks)
logInfo('loaded')
