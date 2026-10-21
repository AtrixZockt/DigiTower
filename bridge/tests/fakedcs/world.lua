-- A small fake of the DCS scripting API, just enough to run the bridge without the game.
-- Lua 5.1. Used by test_bridge.lua and fake_dcs.lua.

local json = require('json')

local M = {}

-- Real Al Dhafra values (from an airbase_info dump of DCS 2.9.30), so the app can route on the real network.
local FIELD_ELEVATION = 16
local AL_DHAFRA = { x = -209935.48, z = -174592.06 }
local SPOT_106 = { x = -211546.62, z = -171721.41 }
local SPOT_60 = { x = -210836.98, z = -172642.11 }

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
    templates = {},      -- group templates of the mission file (env.mission), for group_hold
    scheduled = {},      -- timer.scheduleFunction
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
  function Unit:getCountry() return self.country or 2 end
  -- An F-16-sized box: the nose 7.5 m ahead of the reference point.
  function Unit:getDesc() return { box = { min = { x = -7.5, y = -2, z = -5 }, max = { x = 7.5, y = 3, z = 5 } } } end

  local Group = {}
  Group.__index = Group
  function Group:getName() return self.name end
  function Group:getID() return self.id end
  function Group:getUnits() return self.units end
  function Group:destroy()
    for i, g in ipairs(W.groups) do
      if g == self then table.remove(W.groups, i) break end
    end
    for _, u in ipairs(self.units) do u.alive = false end
    W.print(string.format('[group] %s removed', self.name))
  end
  -- DCS's Start command starts an uncontrolled group: engines about 2 s later, rolling a few seconds after that (W:step).
  function Group:getController()
    local g = self
    return {
      setCommand = function(_, cmd)
        g.commands = g.commands or {}
        g.commands[#g.commands + 1] = cmd.id
        if cmd.id == 'Start' and g.uncontrolled then g.startedAt = W.time end
      end,
      setTask = function(_, task) g.task = task end,
    }
  end

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
    g.uncontrolled = def.uncontrolled
    if def.start then
      local kind = ({ cold = 'TakeOffParking', hot = 'TakeOffParkingHot', runway = 'TakeOff' })[def.start] or 'Turning Point'
      local points = { { type = kind } }
      if def.landAt then points[2] = { type = 'Land', action = 'Landing', airdromeId = def.landAt.id, x = def.landAt.x, y = def.landAt.z } end
      self.templates[#self.templates + 1] = { name = def.group, uncontrolled = def.uncontrolled, route = { points = points },
        units = { { name = def.name, type = def.typeName, x = def.x, y = def.z, heading = math.rad(def.hdg or 0), callsign = def.callsign } } }
    end
    return u
  end

  --- An arriving AI group, roughly: steer to the next point (its own route: the field), circle at a holding point until
  -- the hold's flag is 1, land within 500 m of the field.
  function W:flyArrival(g, dt)
    local u = g.units[1]
    local points = g.task and g.task.params.route.points
    g.wp = g.wp or 1
    local target = points and points[g.wp] or { x = AL_DHAFRA.x, y = AL_DHAFRA.z, alt = FIELD_ELEVATION }
    local dx, dz = target.x - u.x, target.y - u.z
    local dist = math.sqrt(dx * dx + dz * dz)
    local orbit = points and points[g.wp].task and points[g.wp].task.params.tasks[1]
    if orbit and dist < 2000 and self.flags[orbit.params.stopCondition.userFlag] ~= 1 then
      u.hdg = u.hdg + math.rad(3) * dt   -- holding
    elseif points and g.wp < #points and dist < 1000 then
      g.wp = g.wp + 1
    else
      u.hdg = math.atan2(dz, dx)
      u.y = u.y + math.max(-20, math.min(20, (target.alt or FIELD_ELEVATION) - u.y)) * dt
    end
    local fx, fz = AL_DHAFRA.x - u.x, AL_DHAFRA.z - u.z
    if (not points or g.wp == #points) and math.sqrt(fx * fx + fz * fz) < 500 then
      g.landed, u.speed, u.y, u.climb = true, 0, FIELD_ELEVATION, 0
      self:fire('runway_touch', u, self.airbases['Al Dhafra AFB'])
      self:fire('land', u, self.airbases['Al Dhafra AFB'])
      self.print(string.format('[AI] %s landed', u.name))
    end
  end

  --- A unit by name (after a respawn it is a new object).
  function W:unit(name)
    for _, g in ipairs(self.groups) do
      for _, u in ipairs(g.units) do if u.name == name then return u end end
    end
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

  -- The terrain's airdromes (GUI side, for airbase_radios), as on the Persian Gulf map: radio ids, and each radio's
  -- frequencies { hz, modulation } (index 0 first, as DCS returns them).
  W.airdromes = {
    [4] = { display_name = 'Al Dhafra AFB', radio = { 'airfield4_0' } },
    [8] = { display_name = 'Tunb Island AFB' },
    [12] = { display_name = 'Al Minhad AFB', radio = { 'airfield12_0' } },
    [30] = { abandoned = true },
  }
  W.atcRadios = {
    airfield4_0 = { [0] = { 4300000, 0 }, [1] = { 39500000, 0 }, [2] = { 126500000, 0 }, [3] = { 251100000, 0 } },
    airfield12_0 = { [0] = { 3800000, 0 }, [1] = { 38500000, 0 }, [2] = { 118550000, 0 }, [3] = { 250100000, 0 } },
  }

  -- A FOB east of Al Dhafra as Retribution builds them: single-pad invisible FARPs (airbases of category 1), all on
  -- the default FARP frequency and callsign; in the mission file as statics of category "Heliports".
  local Farp = setmetatable({}, { __index = Airbase })
  Farp.__index = Farp
  function Farp:getDesc() return { category = 1 } end
  function Farp:getRunways() return {} end
  function Farp:getParking()
    return { { Term_Index = 1, Term_Index_0 = 0, Term_Type = 40, vTerminalPos = { x = self.x, y = FIELD_ELEVATION, z = self.z }, fDistToRW = 0, TO_AC = false } }
  end
  W.farpStatics = {}
  for i = 0, 1 do
    local name = 'FOB Test Invisible FARP ' .. i
    local x, z = AL_DHAFRA.x + 500 + 150 * i, AL_DHAFRA.z + 6000
    W.airbases[name] = setmetatable({ name = name, id = 100 + i, callsign = 'London', x = x, z = z }, Farp)
    W.farpStatics[#W.farpStatics + 1] = { units = { { category = 'Heliports', type = 'Invisible FARP', name = name, x = x, y = z,
      heliport_frequency = 127.5, heliport_modulation = 0, heliport_callsign_id = 1 } } }
  end

  -- The scripting environment ------------------------------------------------------------------------

  local env = setmetatable({}, { __index = _G })
  W.env = env

  env.Group = {
    Category = { AIRPLANE = 0, HELICOPTER = 1, GROUND = 2, SHIP = 3, TRAIN = 4 },
    getByName = function(name)
      for _, g in ipairs(W.groups) do if g.name == name then return g end end
    end,
  }
  env.Object = { Category = { UNIT = 1, WEAPON = 2, STATIC = 3, BASE = 4, SCENERY = 5, CARGO = 6 } }

  env.coalition = {
    getGroups = function(side, cat)
      local out = {}
      for _, g in ipairs(W.groups) do
        if g.coal == side and g.cat == cat then out[#out + 1] = g end
      end
      return out
    end,
    -- Spawns a group from a template (as group_hold does): planes only, on the ground, at the units' x / y.
    addGroup = function(country, cat, tpl)
      local first
      for _, ut in ipairs(tpl.units or {}) do
        local u = W:addUnit({ name = ut.name, group = tpl.name, typeName = ut.type, cat = cat, coal = 2, callsign = ut.callsign,
          x = ut.x, y = FIELD_ELEVATION, z = ut.y, hdg = math.deg(ut.heading or 0), uncontrolled = tpl.uncontrolled })
        first = first or u
        u.group.country = country
      end
      W.print(string.format('[group] %s spawned%s', tpl.name, tpl.uncontrolled and ', uncontrolled' or ''))
      return first and first.group
    end,
  }
  env.Unit = {
    getByName = function(name)
      for _, g in ipairs(W.groups) do
        for _, u in ipairs(g.units) do if u.name == name then return u end end
      end
    end,
  }


  env.world = {
    -- No fog.
    weather = { getFogVisibilityDistance = function() return 0 end, getFogThickness = function() return 0 end },
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
  env.timer = {
    getTime = function() return W.time end,
    getAbsTime = function() return 8 * 3600 + W.time end,
    scheduleFunction = function(fn, arg, t) W.scheduled[#W.scheduled + 1] = { fn = fn, arg = arg, t = t } end,
  }
  env.env = { info = function(s) W.print('[env.info] ' .. tostring(s)) end,
    mission = { theatre = 'PersianGulf', date = { Year = 2026, Month = 10, Day = 18 }, start_time = 8 * 3600,
    coalition = { blue = { country = { { plane = { group = W.templates }, static = { group = W.farpStatics } } } } },
    -- Few clouds at 8300 ft (preset 1 at its default base), 80 km visibility.
    weather = { visibility = { distance = 80000 }, enable_dust = false, dust_density = 0,
      clouds = { base = 2520, thickness = 200, density = 0, preset = 'Preset1', iprecptns = 0 } } } }

  local function recordText(target, text, seconds)
    W.texts[#W.texts + 1] = { target = target, text = text, seconds = seconds }
    W.print(string.format('[F10 text -> %s] %s', target, text))
  end
  W.marks = {}   -- [id] = { kind = 'line' | 'text', ... }
  W.flags = {}   -- user flags (trigger.action.setUserFlag)
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
      markToGroup = function(id, text, p, groupId)
        W.marks[id] = { kind = 'pin', group = groupId, point = p, text = text }
        W.print(string.format('[F10 map] pin for group %s: "%s"', tostring(groupId), text))
      end,
      removeMark = function(id) W.marks[id] = nil end,
      setUserFlag = function(flag, value) W.flags[flag] = value end,
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
    if opts.ai then
      -- An AI flight parked uncontrolled (as Liberation / Retribution do); the mission starts it after 8 s. Like the real
      -- AI (measured): engines about 2 s after a start, rolling a few seconds later.
      self.enfield = self:addUnit({ name = 'Enfield 1-1', group = 'Enfield 1', typeName = 'F-16C_50', cat = 0, coal = 2,
        callsign = 'Enfield11', x = SPOT_60.x, y = FIELD_ELEVATION, z = SPOT_60.z, hdg = 220, start = 'cold', uncontrolled = true })
      table.insert(self.script, { t = 8, run = function() self.enfield.group.startedAt = self.time end })
    end
    if opts.arrival then
      -- Two AI F-16s coming back to Al Dhafra (their mission routes land there), 12 and 14 NM out.
      local function arriving(name, group, bearing, miles, feet)
        local b = math.rad(bearing)
        local u = self:addUnit({ name = name, group = group, typeName = 'F-16C_50', cat = 0, coal = 2, callsign = name:gsub('[ -]', ''),
          x = AL_DHAFRA.x + math.cos(b) * miles * 1852, y = feet * 0.3048, z = AL_DHAFRA.z + math.sin(b) * miles * 1852,
          hdg = (bearing + 180) % 360, speed = 150, start = 'air', landAt = { id = 4, x = AL_DHAFRA.x, z = AL_DHAFRA.z } })
        u.group.arriving = true
        return u
      end
      self.colt1 = arriving('Colt 1-1', 'Colt 1', 128, 12, 6000)
      self.colt2 = arriving('Colt 2-1', 'Colt 2', 20, 14, 9000)
    end
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
    -- Started AI groups: engines 2 s after the start, rolling 4 s after the engines (a held group waits for Start).
    local ab = self.airbases['Al Dhafra AFB']
    for _, g in ipairs(self.groups) do
      if g.startedAt and not g.enginesAt and self.time >= g.startedAt + 2 then
        g.enginesAt = self.time
        for _, u in ipairs(g.units) do self:fire('engine_startup', u, ab) end
        self.print(string.format('[AI] %s engines running', g.name))
      elseif g.enginesAt and not g.rolling and self.time >= g.enginesAt + 4 then
        g.rolling = true
        for _, u in ipairs(g.units) do u.speed = 6 end
        self.print(string.format('[AI] %s starts to taxi', g.name))
      end
    end
    for _, g in ipairs(self.groups) do
      if g.arriving and not g.landed then self:flyArrival(g, dt) end
    end
    for _, s in ipairs(self.scheduled) do
      if s.t and self.time >= s.t then
        local nextTime = s.fn(s.arg, self.time)
        s.t = type(nextTime) == 'number' and nextTime or nil
      end
    end
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
--- Loads the real hook; `config` (optional) is what Scripts/DigiTower/DigiTowerConfig.lua returns (the port is added), or a
-- string: the file does not load, with that error.
function M.loadHook(W, bridgeRoot, port, startTime, config)
  local callbacks
  local sim = {
    getMissionName = function() return 'Fake Al Dhafra test' end,
    isMultiplayer = function() return false end,
    isServer = function() return true end,
    getRealTime = function() return require('socket').gettime() - startTime end,
    setUserCallbacks = function(cb) callbacks = cb end,
  }
  -- The terrain's airdromes and DCS's ATC radio data, as the probe on the real Persian Gulf map returned them.
  package.loaded['terrain'] = { GetTerrainConfig = function(what) if what == 'Airdromes' then return W.airdromes end end }
  package.loaded['DCS'] = { getATCradiosData = function(radioId) return W.atcRadios[radioId] end }
  -- The hook listens on the test's port whatever its settings say: a hook without settings must never take the
  -- default port (on Windows a second listener can share it with a running DCS and catch its ATC app).
  local realSocket = require('socket')
  local testSocket = setmetatable({
    bind = function(host, p, ...) return realSocket.bind(host, port or p, ...) end,
  }, { __index = realSocket })
  local hookEnv = setmetatable({
    require = function(name) if name == 'socket' then return testSocket end return require(name) end,
    Sim = sim,
    net = W.net,
    log = { INFO = 'INFO', ERROR = 'ERROR', write = function(sub, level, msg) W.print(string.format('[%s %s] %s', sub, level, msg)) end },
    lfs = { writedir = function() return bridgeRoot end },
    loadfile = function(path)
      if path:match('DigiTowerConfig%.lua$') then
        if type(config) == 'string' then return nil, config end
        return function()
          local c = { port = port }
          for k, v in pairs(config or {}) do c[k] = v end
          return c
        end
      end
      return loadfile(path)
    end,
  }, { __index = _G })
  local chunk = assert(loadfile(bridgeRoot .. 'Scripts/Hooks/DigiTowerHook.lua'))
  setfenv(chunk, hookEnv)
  chunk()
  return callbacks
end

return M
