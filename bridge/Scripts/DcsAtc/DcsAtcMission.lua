-- DCS-ATC bridge, mission side.
--
-- Injected into the mission scripting environment by Hooks/DcsAtcBridgeHook.lua when a mission
-- starts. It has no sockets: the hook calls ATCB.poll() a few times per second to collect unit
-- states and queued messages, and forwards commands from the ATC app to ATCB.command().
-- Only the commands in COMMANDS can be executed; the app never sends Lua code.
--
-- Lua 5.1 (DCS). Coordinates are DCS map coordinates: x = north, z = east, y = altitude (m).

ATCB = ATCB or {}
local ATCB = ATCB

ATCB.protocol = 1
ATCB.maxQueue = 5000

---------------------------------------------------------------------------------------------------
-- JSON encoder (the scripting environment has none)
---------------------------------------------------------------------------------------------------

local ARRAY_MT = {}

--- Marks a table as a JSON array, so that it encodes as [] even when empty.
local function array(t)
  return setmetatable(t or {}, ARRAY_MT)
end
ATCB.array = array

local ESCAPES = { ['"'] = '\\"', ['\\'] = '\\\\', ['\b'] = '\\b', ['\f'] = '\\f', ['\n'] = '\\n', ['\r'] = '\\r', ['\t'] = '\\t' }

local function encodeString(s)
  return '"' .. s:gsub('[%c"\\]', function(c)
    return ESCAPES[c] or string.format('\\u%04x', c:byte())
  end) .. '"'
end

local function encodeValue(v, buf, depth)
  if depth > 32 then error('json: nesting too deep') end
  local t = type(v)
  if t == 'nil' then
    buf[#buf + 1] = 'null'
  elseif t == 'boolean' then
    buf[#buf + 1] = v and 'true' or 'false'
  elseif t == 'number' then
    if v ~= v or v == math.huge or v == -math.huge then
      buf[#buf + 1] = 'null'
    elseif v == math.floor(v) and math.abs(v) < 1e15 then
      buf[#buf + 1] = string.format('%d', v)
    else
      buf[#buf + 1] = string.format('%.10g', v)
    end
  elseif t == 'string' then
    buf[#buf + 1] = encodeString(v)
  elseif t == 'table' then
    if getmetatable(v) == ARRAY_MT or #v > 0 then
      buf[#buf + 1] = '['
      for i = 1, #v do
        if i > 1 then buf[#buf + 1] = ',' end
        encodeValue(v[i], buf, depth + 1)
      end
      buf[#buf + 1] = ']'
    else
      buf[#buf + 1] = '{'
      local first = true
      for k, val in pairs(v) do
        if val ~= nil and type(val) ~= 'function' then
          if not first then buf[#buf + 1] = ',' end
          first = false
          buf[#buf + 1] = encodeString(tostring(k))
          buf[#buf + 1] = ':'
          encodeValue(val, buf, depth + 1)
        end
      end
      buf[#buf + 1] = '}'
    end
  else
    buf[#buf + 1] = 'null'
  end
end

function ATCB.encode(v)
  local buf = {}
  encodeValue(v, buf, 0)
  return table.concat(buf)
end

---------------------------------------------------------------------------------------------------
-- Outgoing message queue
---------------------------------------------------------------------------------------------------

ATCB.queue = array()

function ATCB.push(msg)
  local q = ATCB.queue
  if #q >= ATCB.maxQueue then
    table.remove(q, 1)
  end
  q[#q + 1] = msg
end

local function now()
  return timer.getTime()
end

---------------------------------------------------------------------------------------------------
-- Unit states
---------------------------------------------------------------------------------------------------

local GROUP_CATEGORIES = {
  { id = Group.Category.AIRPLANE, name = 'plane' },
  { id = Group.Category.HELICOPTER, name = 'heli' },
}

local function unitState(u, catName)
  local p = u:getPoint()
  local pos = u:getPosition()
  local v = u:getVelocity()
  local hdg = math.atan2(pos.x.z, pos.x.x)
  if hdg < 0 then hdg = hdg + 2 * math.pi end
  local g = u:getGroup()
  return {
    id = tonumber(u:getID()),
    name = u:getName(),
    type = u:getTypeName(),
    cat = catName,
    coal = u:getCoalition(),
    group = g and g:getName() or nil,
    groupId = g and tonumber(g:getID()) or nil,
    player = u:getPlayerName(),
    callsign = u:getCallsign(),
    x = p.x, y = p.y, z = p.z,
    vx = v.x, vy = v.y, vz = v.z,
    hdg = math.deg(hdg),
    agl = p.y - land.getHeight({ x = p.x, y = p.z }),
    inAir = u:inAir(),
  }
end

function ATCB.collectUnits()
  local list = array()
  for side = 0, 2 do
    for _, cat in ipairs(GROUP_CATEGORIES) do
      for _, g in ipairs(coalition.getGroups(side, cat.id) or {}) do
        for _, u in ipairs(g:getUnits() or {}) do
          if u:isExist() and u:isActive() then
            local ok, st = pcall(unitState, u, cat.name)
            if ok then list[#list + 1] = st end
          end
        end
      end
    end
  end
  return list
end

---------------------------------------------------------------------------------------------------
-- Events
---------------------------------------------------------------------------------------------------

local EVENT_NAMES = {}
for k, v in pairs(world.event) do
  if type(k) == 'string' and k:sub(1, 8) == 'S_EVENT_' then
    EVENT_NAMES[v] = k:sub(9):lower()
  end
end

local WANTED_EVENTS = {
  birth = true, player_enter_unit = true, player_leave_unit = true,
  engine_startup = true, engine_shutdown = true,
  takeoff = true, runway_takeoff = true, land = true, runway_touch = true,
  crash = true, dead = true, pilot_dead = true, ejection = true, unit_lost = true,
}

local function describeInitiator(msg, obj)
  msg.unit = obj:getName()
  if obj.getID then msg.unitId = tonumber(obj:getID()) end
  if obj.getTypeName then msg.unitType = obj:getTypeName() end
  if obj.getCoalition then msg.coal = obj:getCoalition() end
  if obj.getPlayerName then msg.player = obj:getPlayerName() end
  if obj.getGroup then
    local g = obj:getGroup()
    if g then
      msg.group = g:getName()
      msg.groupId = tonumber(g:getID())
    end
  end
end

ATCB.eventHandler = ATCB.eventHandler or {}

function ATCB.eventHandler:onEvent(e)
  local name = EVENT_NAMES[e.id]
  if not name or not WANTED_EVENTS[name] then return end
  local msg = { type = 'event', ev = name, t = e.time }
  if e.initiator then pcall(describeInitiator, msg, e.initiator) end
  if e.place then pcall(function() msg.place = e.place:getName() end) end
  ATCB.push(msg)
end

---------------------------------------------------------------------------------------------------
-- F10 menus
---------------------------------------------------------------------------------------------------

ATCB.menus = {}  -- [groupId] = { subs = { [pathKey] = dcsPath }, items = { [id] = dcsPath } }

local SEP = '\31'

local function menusFor(groupId)
  local m = ATCB.menus[groupId]
  if not m then
    m = { subs = {}, items = {} }
    ATCB.menus[groupId] = m
  end
  return m
end

function ATCB.onMenu(arg)
  ATCB.push({ type = 'menu', groupId = arg.groupId, id = arg.id, t = now() })
end

local function menuAdd(c)
  local gid = assert(tonumber(c.groupId), 'groupId required')
  local m = menusFor(gid)
  local parent, walked = nil, {}
  for _, name in ipairs(c.path or {}) do
    walked[#walked + 1] = tostring(name)
    local key = table.concat(walked, SEP)
    if not m.subs[key] then
      m.subs[key] = missionCommands.addSubMenuForGroup(gid, tostring(name), parent)
    end
    parent = m.subs[key]
  end
  local id = assert(c.id, 'id required')
  if m.items[id] then
    missionCommands.removeItemForGroup(gid, m.items[id].path)
  end
  m.items[id] = {
    key = table.concat(walked, SEP),
    path = missionCommands.addCommandForGroup(gid, tostring(c.label or id), parent, ATCB.onMenu, { groupId = gid, id = id }),
  }
end

local function menuRemove(c)
  local gid = assert(tonumber(c.groupId), 'groupId required')
  local m = menusFor(gid)
  local item = m.items[c.id]
  if item then
    missionCommands.removeItemForGroup(gid, item.path)
    m.items[c.id] = nil
  end
end

local function menuClear(c)
  local gid = assert(tonumber(c.groupId), 'groupId required')
  local m = menusFor(gid)
  -- Removing a submenu removes everything below it, so only top-level entries need removing.
  for key, path in pairs(m.subs) do
    if not key:find(SEP, 1, true) then
      missionCommands.removeItemForGroup(gid, path)
    end
  end
  for _, item in pairs(m.items) do
    if item.key == '' then
      missionCommands.removeItemForGroup(gid, item.path)
    end
  end
  ATCB.menus[gid] = nil
end

---------------------------------------------------------------------------------------------------
-- Commands
---------------------------------------------------------------------------------------------------

local function text(c)
  local seconds = tonumber(c.seconds) or 10
  local clear = c.clear and true or false
  local msg = tostring(c.text or '')
  if c.groupId then
    trigger.action.outTextForGroup(tonumber(c.groupId), msg, seconds, clear)
  elseif c.unitId then
    trigger.action.outTextForUnit(tonumber(c.unitId), msg, seconds, clear)
  elseif c.coalition then
    trigger.action.outTextForCoalition(tonumber(c.coalition), msg, seconds, clear)
  else
    trigger.action.outText(msg, seconds, clear)
  end
end

local function airbaseList()
  local out = array()
  for _, ab in ipairs(world.getAirbases() or {}) do
    local p = ab:getPoint()
    local desc = ab:getDesc() or {}
    out[#out + 1] = {
      name = ab:getName(), id = tonumber(ab:getID()), category = desc.category,
      coalition = ab:getCoalition(), x = p.x, y = p.y, z = p.z,
    }
  end
  return out
end

local function airbaseInfo(c)
  local ab = Airbase.getByName(c.name)
  if not ab then error('airbase not found: ' .. tostring(c.name)) end
  local p = ab:getPoint()
  local desc = ab:getDesc() or {}
  local runways = array()
  for _, r in ipairs(ab:getRunways() or {}) do
    runways[#runways + 1] = {
      name = r.Name, course = r.course, width = r.width, length = r.length,
      x = r.position.x, y = r.position.y, z = r.position.z,
    }
  end
  local parking = array()
  for _, s in ipairs(ab:getParking() or {}) do
    parking[#parking + 1] = {
      index = s.Term_Index, index0 = s.Term_Index_0, termType = s.Term_Type,
      x = s.vTerminalPos.x, y = s.vTerminalPos.y, z = s.vTerminalPos.z,
      distToRunway = s.fDistToRW, occupied = s.TO_AC,
    }
  end
  local silent = nil
  if ab.getRadioSilentMode then silent = ab:getRadioSilentMode() end
  return {
    name = ab:getName(), id = tonumber(ab:getID()), callsign = ab:getCallsign(),
    category = desc.category, coalition = ab:getCoalition(),
    x = p.x, y = p.y, z = p.z, radioSilent = silent,
    runways = runways, parking = parking,
  }
end

local function radioSilent(c)
  local ab = Airbase.getByName(c.name)
  if not ab then error('airbase not found: ' .. tostring(c.name)) end
  ab:setRadioSilentMode(c.silent and true or false)
  return { name = c.name, silent = ab:getRadioSilentMode() }
end

local COMMANDS = {
  ping = function() return { pong = true, t = now() } end,
  text = text,
  menu_add = menuAdd,
  menu_remove = menuRemove,
  menu_clear = menuClear,
  airbase_list = airbaseList,
  airbase_info = airbaseInfo,
  radio_silent = radioSilent,
}

--- Executes one command table from the ATC app. Replies are queued when the command has a `req` id.
function ATCB.command(c)
  local fn = COMMANDS[c.cmd]
  local ok, result
  if fn then
    ok, result = pcall(fn, c)
  else
    ok, result = false, 'unknown command: ' .. tostring(c.cmd)
  end
  if c.req then
    ATCB.push({ type = 'reply', req = c.req, cmd = c.cmd, ok = ok, data = ok and result or nil, error = (not ok) and tostring(result) or nil })
  elseif not ok then
    ATCB.push({ type = 'error', cmd = c.cmd, error = tostring(result) })
  end
end

---------------------------------------------------------------------------------------------------
-- Entry points used by the hook
---------------------------------------------------------------------------------------------------

--- Returns everything queued since the last call, plus unit states when `withUnits` is true, as JSON.
function ATCB.poll(withUnits)
  local msgs = ATCB.queue
  ATCB.queue = array()
  local out = { type = 'poll', t = now(), msgs = msgs }
  if withUnits then out.units = ATCB.collectUnits() end
  return ATCB.encode(out)
end

--- Registers the event handler once and returns mission information as JSON.
function ATCB.start()
  if not ATCB.handlerAdded then
    world.addEventHandler(ATCB.eventHandler)
    ATCB.handlerAdded = true
  end
  local m = env.mission or {}
  return ATCB.encode({
    protocol = ATCB.protocol,
    theatre = m.theatre,
    date = m.date,
    startTime = m.start_time,
    absTime = timer.getAbsTime(),
    modelTime = now(),
  })
end
