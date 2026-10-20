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
  if not name or not (WANTED_EVENTS[name] or ATCB.allEvents) then return end
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

--- The FARPs of the mission file (statics of category "Heliports"): name, type, position, and the frequency and
-- callsign the mission gives them (what DCS's own FARP ATC uses); with their pads (parking spots), coalition and
-- callsign name from DCS now.
local function farpList()
  local out = array()
  local sides = { red = 1, blue = 2 }
  for sideName, side in pairs((env.mission or {}).coalition or {}) do
    for _, country in ipairs(side.country or {}) do
      for _, g in ipairs((country.static and country.static.group) or {}) do
        for _, u in ipairs(g.units or {}) do
          if u.category == 'Heliports' then
            local name = u.name
            if type(name) == 'string' and name:sub(1, 8) == 'DictKey_' and env.getValueDictByKey then name = env.getValueDictByKey(name) end
            local farp = {
              name = name, type = u.type, x = u.x, z = u.y, coalition = sides[sideName] or 0,
              frequency = tonumber(u.heliport_frequency), modulation = tonumber(u.heliport_modulation) or 0,
              callsignId = tonumber(u.heliport_callsign_id), pads = array(),
            }
            local ab = Airbase.getByName(name)
            if ab then
              farp.id = tonumber(ab:getID())
              farp.coalition = ab:getCoalition()
              farp.callsign = ab:getCallsign()
              for _, s in ipairs(ab:getParking() or {}) do
                farp.pads[#farp.pads + 1] = { index = s.Term_Index, x = s.vTerminalPos.x, z = s.vTerminalPos.z }
              end
            end
            out[#out + 1] = farp
          end
        end
      end
    end
  end
  return out
end

local function radioSilent(c)
  local ab = Airbase.getByName(c.name)
  if not ab then error('airbase not found: ' .. tostring(c.name)) end
  ab:setRadioSilentMode(c.silent and true or false)
  return { name = c.name, silent = ab:getRadioSilentMode() }
end

--- Wind 10 m above the field (the direction the wind blows towards, m/s) and temperature (K) and
-- pressure (Pa) at field elevation.
local function airbaseWeather(c)
  local ab = Airbase.getByName(c.name)
  if not ab then error('airbase not found: ' .. tostring(c.name)) end
  local p = ab:getPoint()
  local wind = atmosphere.getWind({ x = p.x, y = p.y + 10, z = p.z })
  local temperature, pressure = atmosphere.getTemperatureAndPressure({ x = p.x, y = p.y, z = p.z })
  return { windX = wind.x, windZ = wind.z, temperature = temperature, pressure = pressure, elevation = p.y }
end

--- The mission's weather as set in the editor (clouds, visibility, dust) and the current fog, for ATIS,
--- with the mission clock (seconds since midnight of the start date) for its time.
local function missionWeather()
  local w = (env.mission or {}).weather or {}
  local clouds = w.clouds or {}
  local fogVisibility, fogThickness
  if world.weather and world.weather.getFogVisibilityDistance then
    local ok, v = pcall(world.weather.getFogVisibilityDistance)
    if ok then fogVisibility = v end
    local ok2, t = pcall(world.weather.getFogThickness)
    if ok2 then fogThickness = t end
  elseif w.enable_fog and w.fog then
    fogVisibility, fogThickness = w.fog.visibility, w.fog.thickness
  end
  return {
    visibility = (w.visibility or {}).distance,
    dust = w.enable_dust and w.dust_density or nil,
    fogVisibility = fogVisibility,
    fogThickness = fogThickness,
    cloudBase = clouds.base,
    cloudThickness = clouds.thickness,
    cloudDensity = clouds.density,
    cloudPreset = clouds.preset,
    precipitation = clouds.iprecptns,
    absTime = timer.getAbsTime(),
  }
end

---------------------------------------------------------------------------------------------------
-- F10 map drawings (lines and a text), grouped under a key so they can be replaced or removed
---------------------------------------------------------------------------------------------------

ATCB.drawings = {}         -- [key] = list of mark ids
ATCB.nextMarkId = 7400000  -- well above ids that missions and players typically use

local function newMarkId()
  local id = ATCB.nextMarkId
  ATCB.nextMarkId = id + 1
  return id
end

local function drawingClear(c)
  for _, id in ipairs(ATCB.drawings[c.key] or {}) do trigger.action.removeMark(id) end
  ATCB.drawings[c.key] = nil
end

--- A mark point with a label meant for one group only (trigger.action.markToGroup); removed with drawing_clear and
-- its key. DCS can only draw lines and text for a whole side; whether others really do not see this is to be tested.
local function markGroup(c)
  assert(c.key, 'key required')
  drawingClear(c)
  local id = newMarkId()
  trigger.action.markToGroup(id, tostring(c.text or ''), { x = tonumber(c.x) or 0, y = 0, z = tonumber(c.z) or 0 }, tonumber(c.groupId), true, '')
  ATCB.drawings[c.key] = { id }
  return { id = id }
end

--- Draws a polyline (points = {{x, z}, ...}) for a coalition (-1 = all), optionally with a text at its end.
local function drawRoute(c)
  assert(c.key, 'key required')
  drawingClear(c)
  local coal = tonumber(c.coalition) or -1
  local color = c.color or { 1, 0, 1, 1 }
  local pts = c.points or {}
  local ids = {}
  for i = 2, #pts do
    local id = newMarkId()
    trigger.action.lineToAll(coal, id, { x = pts[i - 1][1], y = 0, z = pts[i - 1][2] }, { x = pts[i][1], y = 0, z = pts[i][2] },
      color, tonumber(c.lineType) or 1, true, '')
    ids[#ids + 1] = id
  end
  if c.text and #pts > 0 then
    local id = newMarkId()
    trigger.action.textToAll(coal, id, { x = pts[#pts][1], y = 0, z = pts[#pts][2] }, color, { 0, 0, 0, 0.35 },
      tonumber(c.fontSize) or 14, true, tostring(c.text))
    ids[#ids + 1] = id
  end
  ATCB.drawings[c.key] = ids
  return { marks = #ids }
end

---------------------------------------------------------------------------------------------------
-- AI departures: holding an AI group at its parking spots, and releasing it
---------------------------------------------------------------------------------------------------
-- Measured in DCS 2.9: an AI stopped by an object in front of it never taxis again, even when the object is gone, so
-- an AI departure is held by putting it back into the uncontrolled state (spawned again from the mission file, cold,
-- on its spots) and released with DCS's Start command. Held groups are released when the app goes away.

ATCB.held = ATCB.held or {}   -- [group name] = true while held
ATCB.lastPoll = ATCB.lastPoll or 0
ATCB.holdWatchdog = 60        -- seconds without a poll from the hook before every held group is released

local function groupName(g)
  local name = g.name
  if type(name) == 'string' and name:sub(1, 8) == 'DictKey_' and env.getValueDictByKey then name = env.getValueDictByKey(name) end
  return name
end

--- A group's table in the mission file, with its country id and category name ('plane' or 'helicopter').
local function findTemplate(name)
  local m = env.mission or {}
  for _, side in pairs(m.coalition or {}) do
    for _, country in ipairs(side.country or {}) do
      for _, catName in ipairs({ 'plane', 'helicopter' }) do
        local cat = country[catName]
        for _, g in ipairs((cat and cat.group) or {}) do
          if groupName(g) == name then return g, country.id, catName end
        end
      end
    end
  end
end

local function deepCopy(t)
  if type(t) ~= 'table' then return t end
  local c = {}
  for k, v in pairs(t) do c[k] = deepCopy(v) end
  return c
end

--- Holds an AI group: spawns it again from the mission file, uncontrolled and cold, on its parking spots (engines off,
-- waiting for a Start command). Fails for groups that are not in the mission file (spawned by scripts).
local function groupHold(c)
  local tpl, countryId, catName = findTemplate(c.group)
  if not tpl then error('not in the mission file: ' .. tostring(c.group)) end
  local copy = deepCopy(tpl)
  copy.name = c.group
  for _, u in ipairs(copy.units or {}) do
    if type(u.name) == 'string' and u.name:sub(1, 8) == 'DictKey_' and env.getValueDictByKey then u.name = env.getValueDictByKey(u.name) end
  end
  copy.uncontrolled = true
  copy.lateActivation = nil
  copy.start_time = 0
  local p1 = copy.route and copy.route.points and copy.route.points[1]
  if p1 then p1.type, p1.action = 'TakeOffParking', 'From Parking Area' end
  local g = Group.getByName(c.group)
  if g then g:destroy() end
  coalition.addGroup(countryId, catName == 'helicopter' and Group.Category.HELICOPTER or Group.Category.AIRPLANE, copy)
  ATCB.held[c.group] = true
  return true
end

--- Releases a held group: DCS's Start command (it starts its engines and taxis).
local function groupRelease(c)
  local g = Group.getByName(c.group)
  if not g then error('group not found: ' .. tostring(c.group)) end
  g:getController():setCommand({ id = 'Start', params = {} })
  ATCB.held[c.group] = nil
  return true
end

--- Releases every held group; returns how many there were.
function ATCB.releaseHeld()
  local n = 0
  for name in pairs(ATCB.held) do
    pcall(groupRelease, { group = name })
    ATCB.held[name] = nil
    n = n + 1
  end
  return n
end

local COMMANDS = {
  ping = function() return { pong = true, t = now() } end,
  text = text,
  menu_add = menuAdd,
  menu_remove = menuRemove,
  menu_clear = menuClear,
  airbase_list = airbaseList,
  airbase_info = airbaseInfo,
  farp_list = farpList,
  airbase_weather = airbaseWeather,
  mission_weather = missionWeather,
  radio_silent = radioSilent,
  draw_route = drawRoute,
  drawing_clear = drawingClear,
  mark_group = markGroup,
  group_hold = groupHold,
  group_release = groupRelease,
  -- Diagnostics: forward every DCS event, not only those the app uses.
  debug_events = function(c) ATCB.allEvents = c.on == true; return { allEvents = ATCB.allEvents } end,
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
  ATCB.lastPoll = now()
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
  -- Held AI must never outlive the app: without polls (the app is gone) every held group is released.
  if not ATCB.watchdogScheduled then
    ATCB.watchdogScheduled = true
    ATCB.lastPoll = now()
    timer.scheduleFunction(function()
      if next(ATCB.held) ~= nil and now() - ATCB.lastPoll > ATCB.holdWatchdog then
        local n = ATCB.releaseHeld()
        env.info(string.format('DCS-ATC: no contact with the ATC app for %d s, %d held AI groups released', ATCB.holdWatchdog, n))
      end
      return now() + 15
    end, nil, now() + 15)
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
