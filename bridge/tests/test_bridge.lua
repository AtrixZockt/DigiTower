-- Tests for the DCS-ATC bridge (mission side and hook) against the fake DCS in fakedcs/world.lua.
-- Run from the repository root with Lua 5.1 + LuaSocket (e.g. Lua for Windows):
--   lua bridge/tests/test_bridge.lua

local root = (arg and arg[0] or ''):gsub('\\', '/'):match('^(.*/)tests/[^/]*$') or 'bridge/'
package.path = root .. 'tests/?.lua;' .. package.path

local json = require('json')
local socket = require('socket')
local fake = require('fakedcs.world')

local passed, failed = 0, 0

local function test(name, fn)
  local ok, err = pcall(fn)
  if ok then
    passed = passed + 1
    print('ok    ' .. name)
  else
    failed = failed + 1
    print('FAIL  ' .. name .. '\n      ' .. tostring(err))
  end
end

local function eq(actual, expected, what)
  if actual ~= expected then
    error(string.format('%s: expected %s, got %s', what or 'value', tostring(expected), tostring(actual)), 2)
  end
end

--- Runs code in the fake mission the way the hook does (result returned twice, see runInMission in the hook).
local function runInMission(W, code)
  local inner = 'local __f = function()\n' .. code .. '\nend\nlocal __r = __f()\nreturn __r, __r'
  return W.net.dostring_in('mission', 'local a, b = a_do_script(' .. string.format('%q', inner) .. ')\nif b ~= nil then return b end\nreturn a')
end

--- A fake world with the mission script loaded the same way the hook does it.
local function missionWorld(opts)
  local W = fake.new(opts)
  W:setupScenario()
  local f = assert(io.open(root .. 'Scripts/DcsAtc/DcsAtcMission.lua')):read('*a')
  local info, ok = runInMission(W, f .. '\nreturn ATCB.start()')
  assert(ok and info ~= '', 'mission did not start: ' .. tostring(info))
  return W, W.env.ATCB, info
end

local function poll(W, withUnits)
  return fake.stripNull(json.decode(W.env.ATCB.poll(withUnits)))
end

---------------------------------------------------------------------------------------------------

test('encoder: strings, numbers, arrays, objects', function()
  local W, ATCB = missionWorld()
  eq(ATCB.encode('a"b\\c\n\1'), '"a\\"b\\\\c\\n\\u0001"', 'escaped string')
  eq(ATCB.encode(42), '42', 'integer')
  eq(ATCB.encode(-356110.1473769583), '-356110.1474', 'float')
  eq(ATCB.encode(0 / 0), 'null', 'NaN')
  eq(ATCB.encode(ATCB.array()), '[]', 'empty array')
  eq(ATCB.encode({}), '{}', 'empty object')
  eq(ATCB.encode({ 1, 'x', true }), '[1,"x",true]', 'array')
  local decoded = json.decode(ATCB.encode({ a = 1, b = { c = 'd' } }))
  eq(decoded.a, 1, 'object a'); eq(decoded.b.c, 'd', 'object b.c')
end)

test('fake DCS reproduces the a_do_script return bug', function()
  local buggy = fake.new()
  eq(buggy.net.dostring_in('mission', 'return a_do_script("return 42")'), '', 'single value lost')
  eq(buggy.net.dostring_in('mission', 'local a, b = a_do_script("return 1, 2") return tostring(a) .. "," .. tostring(b)'), 'nil,1', 'values shifted')
  local fixed = fake.new({ fixedReturns = true })
  eq(fixed.net.dostring_in('mission', 'return a_do_script("return 42")'), '42', 'fixed DCS')
end)

test('start returns mission info, with and without the return bug', function()
  for _, fixed in ipairs({ false, true }) do
    local _, _, info = missionWorld({ fixedReturns = fixed })
    local m = json.decode(info)
    eq(m.theatre, 'PersianGulf', 'theatre')
    eq(m.protocol, 1, 'protocol')
  end
end)

test('poll returns unit states for planes and helicopters', function()
  local W = missionWorld()
  local p = poll(W, true)
  eq(p.type, 'poll', 'type')
  eq(#p.units, 3, 'unit count')
  local byName = {}
  for _, u in ipairs(p.units) do byName[u.name] = u end
  local viper = byName['Viper 1-1']
  eq(viper.type, 'F-16C_50', 'type'); eq(viper.cat, 'plane', 'cat'); eq(viper.player, 'DiGii', 'player')
  eq(viper.inAir, false, 'inAir'); eq(math.floor(viper.hdg + 0.5), 40, 'heading')
  eq(byName['Dustoff 1-1'].cat, 'heli', 'helicopter category')
  eq(byName['Texaco 1-1'].player, nil, 'AI has no player')
  eq(poll(W, false).units, nil, 'no units when not requested')
end)

test('events are queued with unit, group and place', function()
  local W = missionWorld()
  for _ = 1, 400 do W:step(0.1) end
  local p = poll(W, false)
  local seen = {}
  for _, m in ipairs(p.msgs) do
    if m.type == 'event' then seen[m.ev] = m end
  end
  eq(seen.birth ~= nil, true, 'birth seen')
  eq(seen.engine_startup.unit, 'Viper 1-1', 'engine_startup unit')
  eq(seen.takeoff.place, 'Al Dhafra AFB', 'takeoff place')
  eq(seen.runway_takeoff.player, 'DiGii', 'runway_takeoff player')
  eq(seen.takeoff.groupId ~= nil, true, 'groupId present')
  eq(#poll(W, false).msgs, 0, 'queue emptied by poll')
end)

test('menus: add, click, remove, clear', function()
  local W, ATCB = missionWorld()
  local gid = W.viper.group.id
  ATCB.command({ cmd = 'menu_add', groupId = gid, path = { 'ATC' }, label = 'Request taxi', id = 'taxi' })
  ATCB.command({ cmd = 'menu_add', groupId = gid, path = { 'ATC', 'Info' }, label = 'ATIS', id = 'atis' })
  ATCB.command({ cmd = 'menu_add', groupId = gid, label = 'Radio check', id = 'check' })
  eq(#W.menuItems[gid], 3, 'three items')
  W:clickMenus(0)
  local p = poll(W, false)
  local clicks = {}
  for _, m in ipairs(p.msgs) do if m.type == 'menu' then clicks[m.id] = m.groupId end end
  eq(clicks.taxi, gid, 'taxi click'); eq(clicks.atis, gid, 'atis click')
  ATCB.command({ cmd = 'menu_remove', groupId = gid, id = 'taxi' })
  eq(#W.menuItems[gid], 2, 'item removed')
  ATCB.command({ cmd = 'menu_clear', groupId = gid })
  eq(#W.menuItems[gid], 0, 'menus cleared')
end)

test('commands: replies, text, airbase info, radio silence, unknown', function()
  local W, ATCB = missionWorld()
  ATCB.command({ cmd = 'ping', req = 1 })
  ATCB.command({ cmd = 'text', groupId = 7, text = 'Hello', seconds = 5 })
  ATCB.command({ cmd = 'airbase_info', req = 2, name = 'Al Dhafra AFB' })
  ATCB.command({ cmd = 'radio_silent', req = 3, name = 'Al Dhafra AFB', silent = true })
  ATCB.command({ cmd = 'nope', req = 4 })
  ATCB.command({ cmd = 'airbase_info', req = 5, name = 'Nowhere' })
  local replies = {}
  for _, m in ipairs(poll(W, false).msgs) do if m.type == 'reply' then replies[m.req] = m end end
  eq(replies[1].ok, true, 'ping ok'); eq(replies[1].data.pong, true, 'pong')
  eq(W.texts[1].text, 'Hello', 'text shown'); eq(W.texts[1].target, 'group 7', 'text target')
  eq(replies[2].data.runways[1].name, '13', 'runway name (DCS calls the parallel runways 13 and 31)')
  eq(#replies[2].data.parking, 4, 'parking count')
  eq(replies[3].data.silent, true, 'radio silent')
  eq(replies[4].ok, false, 'unknown command fails'); eq(replies[5].ok, false, 'missing airbase fails')
end)

local function hookEndToEnd(port, fixedReturns)
  local start = socket.gettime()
  local W = fake.new({ fixedReturns = fixedReturns })
  W:setupScenario()
  local cb = fake.loadHook(W, root, port, start)
  local client = assert(socket.connect('127.0.0.1', port))
  client:settimeout(0)
  local lines = {}
  local buffer = ''
  local function pump(seconds)
    local untilTime = socket.gettime() + seconds
    while socket.gettime() < untilTime do
      W:step(0.05)
      cb.onSimulationFrame()
      W:clickMenus(0.2)
      while true do
        local line, err, partial = client:receive('*l')
        if line then lines[#lines + 1] = fake.stripNull(json.decode(buffer .. line)); buffer = ''
        else
          if partial and partial ~= '' then buffer = buffer .. partial end
          break
        end
      end
      socket.sleep(0.01)
    end
  end
  local function find(pred)
    for _, m in ipairs(lines) do if pred(m) then return m end end
  end

  pump(0.2)
  eq(find(function(m) return m.type == 'hello' end).missionRunning, false, 'hello before mission')
  client:send('{"cmd":"ping","req":1}\n')
  pump(0.2)
  eq(find(function(m) return m.type == 'reply' and m.req == 1 end).ok, false, 'no mission yet')

  cb.onSimulationStart()
  pump(0.2)
  eq(find(function(m) return m.type == 'mission_start' end).info.theatre, 'PersianGulf', 'mission_start')
  client:send('{"cmd":"ping","req":2}\n')
  client:send('{"cmd":"menu_add","groupId":' .. W.viper.group.id .. ',"path":["ATC"],"label":"Ping","id":"ping"}\n')
  pump(1.0)
  local unitsSeen, pong, click = false, nil, nil
  for _, m in ipairs(lines) do
    if m.type == 'poll' then
      if m.units and #m.units == 3 then unitsSeen = true end
      for _, x in ipairs(m.msgs) do
        if x.type == 'reply' and x.req == 2 then pong = x end
        if x.type == 'menu' and x.id == 'ping' then click = x end
      end
    end
  end
  eq(unitsSeen, true, 'unit snapshot received')
  eq(pong and pong.ok, true, 'ping reply in mission')
  eq(click and click.groupId, W.viper.group.id, 'menu click forwarded')

  client:send('this is not json\n')
  pump(0.2)
  eq(find(function(m) return m.type == 'error' end) ~= nil, true, 'bad line reported')

  cb.onSimulationStop()
  pump(0.2)
  eq(find(function(m) return m.type == 'mission_stop' end) ~= nil, true, 'mission_stop')
  client:close()
end

test('commands: weather and F10 map drawings', function()
  local W, ATCB = missionWorld()
  ATCB.command({ cmd = 'airbase_weather', req = 1, name = 'Al Dhafra AFB' })
  ATCB.command({ cmd = 'draw_route', req = 2, key = 'taxi-1', coalition = 2, points = { { 0, 0 }, { 100, 0 }, { 100, 100 } }, text = 'HOLD SHORT 13L' })
  local replies = {}
  for _, m in ipairs(poll(W, false).msgs) do if m.type == 'reply' then replies[m.req] = m end end
  eq(math.floor(replies[1].data.pressure), 101130, 'pressure')
  eq(replies[1].data.elevation, 16, 'elevation')
  eq(replies[2].data.marks, 3, 'two lines and a text')
  local count = 0
  for _ in pairs(W.marks) do count = count + 1 end
  eq(count, 3, 'marks on the map')
  ATCB.command({ cmd = 'draw_route', key = 'taxi-1', coalition = 2, points = { { 0, 0 }, { 50, 0 } } })
  count = 0
  for _ in pairs(W.marks) do count = count + 1 end
  eq(count, 1, 'redraw replaces the old route')
  ATCB.command({ cmd = 'drawing_clear', key = 'taxi-1' })
  eq(next(W.marks), nil, 'cleared')
end)

test('hook: end to end over a real socket (DCS 2.9.30 a_do_script bug)', function() hookEndToEnd(42790, false) end)
test('hook: end to end over a real socket (a_do_script fixed)', function() hookEndToEnd(42791, true) end)

test('hook: reports a failed injection in hello', function()
  local start = socket.gettime()
  local W = fake.new()
  W:setupScenario()
  local cb = fake.loadHook(W, root, 42792, start)
  W.missionState.a_do_script = function() return nil end   -- mission side never answers
  cb.onSimulationStart()
  local client = assert(socket.connect('127.0.0.1', 42792))
  client:settimeout(1)
  for _ = 1, 5 do cb.onSimulationFrame() socket.sleep(0.02) end
  local hello = fake.stripNull(json.decode(assert(client:receive('*l'))))
  eq(hello.missionRunning, false, 'not running')
  eq(hello.error ~= nil and hello.error:find('mission injection failed', 1, true) ~= nil, true, 'error reported')
  client:close()
end)

print(string.format('\n%d passed, %d failed', passed, failed))
os.exit(failed == 0 and 0 or 1)
