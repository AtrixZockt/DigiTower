-- A small fake of the DCS scripting API, just enough to run the bridge without the game.
-- Lua 5.1. Used by test_bridge.lua and fake_dcs.lua.

local json = require('json')

local M = {}

-- Real Al Dhafra values (from an airbase_info dump of DCS 2.9.30), so the app can route on the real network.
local FIELD_ELEVATION = 16
local AL_DHAFRA = { x = -209935.48, z = -174592.06 }
local SPOT_106 = { x = -211546.62, z = -171721.41 }

M.EVENTS = {
  S_EVENT_TAKEOFF = 3, S_EVENT_LAND = 4, S_EVENT_CRASH = 5, S_EVENT_EJECTION = 6, S_EVENT_DEAD = 8,
  S_EVENT_PILOT_DEAD = 9, S_EVENT_BIRTH = 15, S_EVENT_ENGINE_STARTUP = 18, S_EVENT_ENGINE_SHUTDOWN = 19,
  S_EVENT_PLAYER_ENTER_UNIT = 20, S_EVENT_PLAYER_LEAVE_UNIT = 21, S_EVENT_UNIT_LOST = 30,
  S_EVENT_RUNWAY_TAKEOFF = 54, S_EVENT_RUNWAY_TOUCH = 55, S_EVENT_SHOT = 1,
}

--- Recursively replaces the json module's null sentinel with nil.
local function stripNull(v)
  if v == json.null then return nil end
  if type(v) == 'table' then
    for k, val in pairs(v) do v[k] = stripNull(val) end
  end
  return v
end
M.stripNull = stripNull

function M.new(opts)
  opts = opts or {}
  local W = {
    time = 0,
    groups = {},
    handlers = {},
    texts = {},
    menuItems = {},      -- [groupId] = list of { path = {...}, fn, arg, addedAt, clicked }
    airbases = {},
    print = opts.print or function() end,
  }

  -- Units and groups -----------------------------------------------------------------------------

  local Unit = {}
  Unit.__index = Unit
  function Unit:getID() return self.id end
  function Unit:getName() return self.name end
  function Unit:getTypeName() return self.typeName end
  function Unit:getCoalition() return self.coal end
  function Unit:getGroup() return self.group end
  function Unit:getPlayerName() return self.player end
  function Unit:getCallsign() return self.callsign end
  function Unit:getPoint() return { x = self.x, y = self.y, z = self.z } end
  function Unit:getPosition()
    local h = self.hdg
    return {
      p = self:getPoint(),
      x = { x = math.cos(h), y = 0, z = math.sin(h) },
      y = { x = 0, y = 1, z = 0 },
      z = { x = -math.sin(h), y = 0, z = math.cos(h) },
    }
  end
  function Unit:getVelocity()
    return { x = self.speed * math.cos(self.hdg), y = self.climb or 0, z = self.speed * math.sin(self.hdg) }
  end
  function Unit:inAir() return self.y - FIELD_ELEVATION > 3 end
  function Unit:isExist() return self.alive end
  function Unit:isActive() return true end

  local Group = {}
  Group.__index = Group
  function Group:getName() return self.name end
  function Group:getID() return self.id end
  function Group:getUnits() return self.units end

  local nextId = 1
  function W:addUnit(def)
    local g = setmetatable({ name = def.group, id = nextId, units = {}, cat = def.cat, coal = def.coal }, Group)
    local u = setmetatable({
      id = nextId + 1000, name = def.name, typeName = def.typeName, coal = def.coal, group = g,
      player = def.player, callsign = def.callsign or def.name,
      x = def.x, y = def.y, z = def.z, hdg = math.rad(def.hdg or 0), speed = def.speed or 0,
      alive = true,
    }, Unit)
    nextId = nextId + 1
    g.units[1] = u
    self.groups[#self.groups + 1] = g
    return u
  end

  -- Airbases ---------------------------------------------------------------------------------------

  local Airbase = {}
  Airbase.__index = Airbase
  function Airbase:getName() return self.name end
  function Airbase:getID() return self.id end
  function Airbase:getCallsign() return self.callsign end
  function Airbase:getDesc() return { category = 0 } end
  function Airbase:getCoalition() return 2 end
  function Airbase:getPoint() return { x = self.x, y = FIELD_ELEVATION, z = self.z } end
  function Airbase:getRunways() return self.runways end
  function Airbase:getParking() return self.parking end
  function Airbase:setRadioSilentMode(silent) self.silent = silent end
  function Airbase:getRadioSilentMode() return self.silent or false end

  W.airbases['Al Dhafra AFB'] = setmetatable({
    name = 'Al Dhafra AFB', id = 4, callsign = 'Al Dhafra AFB', x = AL_DHAFRA.x, z = AL_DHAFRA.z,
    runways = {
      { Name = '13', course = -2.233061075, width = 60, length = 3475.79, position = { x = -211027.78, y = FIELD_ELEVATION, z = -173240 } },
      { Name = '31', course = -2.233127356, width = 60, length = 3475.79, position = { x = -212946.27, y = FIELD_ELEVATION, z = -172936.47 } },
    },
    parking = {
      { Term_Index = 1, Term_Index_0 = -1, Term_Type = 72, vTerminalPos = { x = -210738.89, y = FIELD_ELEVATION, z = -174976.17 }, fDistToRW = 1584, TO_AC = false },
      { Term_Index = 60, Term_Index_0 = -1, Term_Type = 72, vTerminalPos = { x = -210836.98, y = FIELD_ELEVATION, z = -172642.11 }, fDistToRW = 2933, TO_AC = false },
      { Term_Index = 106, Term_Index_0 = -1, Term_Type = 104, vTerminalPos = { x = SPOT_106.x, y = FIELD_ELEVATION, z = SPOT_106.z }, fDistToRW = 3879, TO_AC = true },
      { Term_Index = 200, Term_Index_0 = -1, Term_Type = 72, vTerminalPos = { x = -213318.59, y = FIELD_ELEVATION, z = -173205.12 }, fDistToRW = 4921, TO_AC = false },
    },
  }, Airbase)

  -- The scripting environment ------------------------------------------------------------------------

  local env = setmetatable({}, { __index = _G })
  W.env = env

  env.Group = { Category = { AIRPLANE = 0, HELICOPTER = 1, GROUND = 2, SHIP = 3, TRAIN = 4 } }
  env.Object = { Category = { UNIT = 1, WEAPON = 2, STATIC = 3, BASE = 4, SCENERY = 5, CARGO = 6 } }

  env.coalition = {
    getGroups = function(side, cat)
      local out = {}
      for _, g in ipairs(W.groups) do
        if g.coal == side and g.cat == cat then out[#out + 1] = g end
      end
      return out
    end,
  }

  env.world = {
    event = M.EVENTS,
    addEventHandler = function(h) W.handlers[#W.handlers + 1] = h end,
    getAirbases = function()
      local out = {}
      for _, ab in pairs(W.airbases) do out[#out + 1] = ab end
      return out
    end,
  }

  env.Airbase = { getByName = function(name) return W.airbases[name] end }
  -- Wind from 310° at 5 m/s (DCS returns the direction the wind blows towards), ISA-ish 30 °C day.
  W.wind = { x = -5 * math.cos(math.rad(310)), y = 0, z = -5 * math.sin(math.rad(310)) }
  env.atmosphere = {
    getWind = function() return W.wind end,
    getTemperatureAndPressure = function() return 303.15, 101130 end,
  }
  env.land = { getHeight = function() return FIELD_ELEVATION end }
  env.timer = { getTime = function() return W.time end, getAbsTime = function() return 8 * 3600 + W.time end }
  env.env = { mission = { theatre = 'PersianGulf', date = { Year = 2026, Month = 10, Day = 18 }, start_time = 8 * 3600 } }

  local function recordText(target, text, seconds)
    W.texts[#W.texts + 1] = { target = target, text = text, seconds = seconds }
    W.print(string.format('[F10 text -> %s] %s', target, text))
  end
  W.marks = {}   -- [id] = { kind = 'line' | 'text', ... }
  env.trigger = {
    action = {
      outText = function(t, s) recordText('all', t, s) end,
      outTextForGroup = function(g, t, s) recordText('group ' .. g, t, s) end,
      outTextForUnit = function(u, t, s) recordText('unit ' .. u, t, s) end,
      outTextForCoalition = function(c, t, s) recordText('coalition ' .. c, t, s) end,
      lineToAll = function(coal, id, a, b) W.marks[id] = { kind = 'line', coalition = coal, from = a, to = b } end,
      textToAll = function(coal, id, p, color, fill, size, readOnly, text)
        W.marks[id] = { kind = 'text', coalition = coal, point = p, text = text }
        local lines = 0
        for _, m in pairs(W.marks) do if m.kind == 'line' then lines = lines + 1 end end
        W.print(string.format('[F10 map] %d route lines on the map, text "%s"', lines, text))
      end,
      removeMark = function(id) W.marks[id] = nil end,
    },
  }

  local function copyPath(parent, name)
    local p = {}
    for i, v in ipairs(parent or {}) do p[i] = v end
    p[#p + 1] = name
    return p
  end
  local function startsWith(path, prefix)
    if #path < #prefix then return false end
    for i = 1, #prefix do if path[i] ~= prefix[i] then return false end end
    return true
  end
  env.missionCommands = {
    addSubMenuForGroup = function(gid, name, parent) return copyPath(parent, name) end,
    addCommandForGroup = function(gid, name, parent, fn, arg)
      local path = copyPath(parent, name)
      W.menuItems[gid] = W.menuItems[gid] or {}
      table.insert(W.menuItems[gid], { path = path, fn = fn, arg = arg, addedAt = W.time })
      W.print(string.format('[F10 menu + group %d] %s', gid, table.concat(path, ' > ')))
      return path
    end,
    removeItemForGroup = function(gid, path)
      local items = W.menuItems[gid] or {}
      for i = #items, 1, -1 do
        if startsWith(items[i].path, path) then table.remove(items, i) end
      end
    end,
  }

  -- The "mission" Lua state, where DCS defines a_do_script ----------------------------------------------

  -- DCS 2.9.18+ (still 2.9.30) passes a_do_script() return values back shifted: a nil is prepended
  -- and the last value dropped. Reproduced here unless opts.fixedReturns is set.
  W.mangleReturns = not opts.fixedReturns

  local function pack(...) return { n = select('#', ...), ... } end

  W.missionState = setmetatable({}, { __index = _G })
  W.missionState.a_do_script = function(code)
      local f, err = loadstring(code)
      if not f then W.print('[a_do_script] ' .. err) return nil end
      setfenv(f, env)
      local res = pack(pcall(f))
      if not res[1] then W.print('[a_do_script] ' .. tostring(res[2])) return nil end
      local values = { n = res.n - 1 }
      for i = 2, res.n do values[i - 1] = res[i] end
      if W.mangleReturns then
        local shifted = { n = values.n }
        for i = values.n, 2, -1 do shifted[i] = values[i - 1] end
        shifted[1] = nil
        values = shifted
      end
      return unpack(values, 1, values.n)
  end

  W.net = {
    lua2json = function(v) return json.encode(v) end,
    json2lua = function(s) return stripNull(json.decode(s)) end,
    dostring_in = function(stateName, code)
      if stateName ~= 'mission' then return 'state not allowed', false end
      local f, err = loadstring(code)
      if not f then return err, false end
      setfenv(f, W.missionState)
      local ok, res = pcall(f)
      if not ok then return tostring(res), false end
      if res == nil then return '', true end
      return tostring(res), true
    end,
  }

  -- Simulation -----------------------------------------------------------------------------------------

  function W:fire(eventName, unit, place)
    local e = { id = M.EVENTS['S_EVENT_' .. eventName:upper()], time = self.time, initiator = unit, place = place }
    for _, h in ipairs(self.handlers) do h:onEvent(e) end
  end

  --- Clicks every menu item that has been in the menu for at least `delay` seconds and was not clicked yet.
  function W:clickMenus(delay, filter)
    for gid, items in pairs(self.menuItems) do
      for _, item in ipairs(items) do
        local label = table.concat(item.path, ' > ')
        if not item.clicked and self.time - item.addedAt >= delay and (not filter or label:find(filter, 1, true)) then
          item.clicked = true
          self.print(string.format('[F10 click group %d] %s', gid, label))
          item.fn(item.arg)
        end
      end
    end
  end

  --- A short scenario at Al Dhafra: a player F-16 starts, taxis and takes off, a tanker orbits,
  -- a player helicopter hovers.
  --- opts.static: the jet stays parked (no scripted taxi and takeoff).
  function W:setupScenario(opts)
    opts = opts or {}
    self.viper = self:addUnit({ name = 'Viper 1-1', group = 'Viper 1', typeName = 'F-16C_50', cat = 0, coal = 2,
      player = 'DiGii', x = SPOT_106.x, y = FIELD_ELEVATION, z = SPOT_106.z, hdg = 40 })
    self.tanker = self:addUnit({ name = 'Texaco 1-1', group = 'Texaco 1', typeName = 'KC-135', cat = 0, coal = 2,
      x = AL_DHAFRA.x + 20000, y = 6000, z = AL_DHAFRA.z, hdg = 90, speed = 200 })
    self.helo = self:addUnit({ name = 'Dustoff 1-1', group = 'Dustoff 1', typeName = 'UH-1H', cat = 1, coal = 2,
      player = 'Wingman', x = AL_DHAFRA.x - 300, y = FIELD_ELEVATION + 10, z = AL_DHAFRA.z + 400, hdg = 310 })
    self.script = {
      { t = 0.5, run = function() self:fire('birth', self.viper); self:fire('birth', self.helo); self:fire('birth', self.tanker) end },
      { t = 3, run = function() self:fire('engine_startup', self.viper) end },
    }
    if not opts.static then
      table.insert(self.script, { t = 6, run = function() self.viper.speed = 7; self.viper.hdg = math.rad(130) end })
      table.insert(self.script, { t = 30, run = function() self.viper.speed = 80 end })
      table.insert(self.script, { t = 36, run = function()
        self.viper.climb = 15
        self:fire('runway_takeoff', self.viper, self.airbases['Al Dhafra AFB'])
        self:fire('takeoff', self.viper, self.airbases['Al Dhafra AFB'])
      end })
    end
  end

  function W:step(dt)
    self.time = self.time + dt
    for _, g in ipairs(self.groups) do
      for _, u in ipairs(g.units) do
        u.x = u.x + u.speed * math.cos(u.hdg) * dt
        u.z = u.z + u.speed * math.sin(u.hdg) * dt
        u.y = u.y + (u.climb or 0) * dt
      end
    end
    if self.tanker then self.tanker.hdg = self.tanker.hdg + math.rad(1.5) * dt end
    for _, s in ipairs(self.script or {}) do
      if not s.done and self.time >= s.t then
        s.done = true
        s.run()
      end
    end
  end

  return W
end

--- Loads the bridge hook into `W` with stubbed Sim/net/log/lfs and returns its callbacks.
function M.loadHook(W, bridgeRoot, port, startTime)
  local callbacks
  local sim = {
    getMissionName = function() return 'Fake Al Dhafra test' end,
    isMultiplayer = function() return false end,
    isServer = function() return true end,
    getRealTime = function() return require('socket').gettime() - startTime end,
    setUserCallbacks = function(cb) callbacks = cb end,
  }
  local hookEnv = setmetatable({
    Sim = sim,
    net = W.net,
    log = { INFO = 'INFO', ERROR = 'ERROR', write = function(sub, level, msg) W.print(string.format('[%s %s] %s', sub, level, msg)) end },
    lfs = { writedir = function() return bridgeRoot end },
    loadfile = function(path)
      if path:match('DcsAtcConfig%.lua$') then
        return function() return { port = port } end
      end
      return loadfile(path)
    end,
  }, { __index = _G })
  local chunk = assert(loadfile(bridgeRoot .. 'Scripts/Hooks/DcsAtcBridgeHook.lua'))
  setfenv(chunk, hookEnv)
  chunk()
  return callbacks
end

return M
