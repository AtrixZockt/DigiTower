-- Runs the real bridge hook against a fake DCS mission at Al Dhafra, so the ATC app can be
-- developed without starting the game.
--   lua bridge/tests/fake_dcs.lua [--port 42700] [--duration 120] [--click [text]] [--static] [--quiet]
--
-- --click [text]  "clicks" every F10 menu item the app adds (or only those whose path contains text),
--                 two seconds after it appears.
-- --static        the jet stays parked (no scripted taxi and takeoff).

local root = (arg and arg[0] or ''):gsub('\\', '/'):match('^(.*/)tests/[^/]*$') or 'bridge/'
package.path = root .. 'tests/?.lua;' .. package.path

local socket = require('socket')
local fake = require('fakedcs.world')

local port, duration, click, clickFilter, quiet, static = 42700, 120, false, nil, false, false
local i = 1
while arg[i] do
  if arg[i] == '--port' then port = tonumber(arg[i + 1]); i = i + 1
  elseif arg[i] == '--duration' then duration = tonumber(arg[i + 1]); i = i + 1
  elseif arg[i] == '--click' then
    click = true
    if arg[i + 1] and arg[i + 1]:sub(1, 2) ~= '--' then clickFilter = arg[i + 1]; i = i + 1 end
  elseif arg[i] == '--static' then static = true
  elseif arg[i] == '--quiet' then quiet = true
  end
  i = i + 1
end

local start = socket.gettime()
local W = fake.new({ print = function(s)
  if not quiet then io.write(string.format('%7.2f  %s\n', socket.gettime() - start, s)); io.flush() end
end })
W:setupScenario({ static = static })
local cb = fake.loadHook(W, root, port, start)

io.write(string.format('fake DCS on 127.0.0.1:%d for %d s (mission starts after 1 s)\n', port, duration))
io.flush()

local started = false
local last = socket.gettime()
while socket.gettime() - start < duration do
  local now = socket.gettime()
  if not started and now - start >= 1 then
    started = true
    cb.onSimulationStart()
  end
  if started then
    W:step(now - last)
    if click then W:clickMenus(2, clickFilter) end
  end
  last = now
  cb.onSimulationFrame()
  socket.sleep(0.02)
end

cb.onSimulationStop()
cb.onSimulationFrame()
io.write('fake DCS finished\n')
