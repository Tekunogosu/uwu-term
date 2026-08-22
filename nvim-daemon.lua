-- Loaded into sessions started by nvim-daemon.sh, and into nothing else.
--
-- :q means "close this window", and a session attached to the game usually has one window
-- per thing on screen and many files behind them as buffers. Quitting a file therefore shut
-- a window - often the whole session, taking every other buffer with it - when what was
-- meant was "I am done with this file". Here :q closes the buffer and leaves the layout
-- alone, which is what the X on a bufferline tab does and what people mean by it.
--
-- :qa still ends the session. The abbreviation below only fires on a command line that is
-- exactly "q", so :qa, :wq and :g/q/d are untouched.

local source = debug.getinfo(1, 'S').source:gsub('^@', '')

local function replacement(closing)
  -- What the windows showing this buffer should show instead. The alternate file first, so
  -- closing one of two files lands on the other rather than somewhere arbitrary.
  local alternate = vim.fn.bufnr('#')
  if alternate > 0 and alternate ~= closing and vim.api.nvim_buf_is_loaded(alternate)
     and vim.bo[alternate].buflisted then
    return alternate
  end

  for _, buffer in ipairs(vim.api.nvim_list_bufs()) do
    if buffer ~= closing and vim.bo[buffer].buflisted and vim.api.nvim_buf_is_loaded(buffer) then
      return buffer
    end
  end

  -- Nothing left to show, so an empty one - the window stays, and the session with it.
  return vim.api.nvim_create_buf(true, false)
end

-- Echoed on a schedule rather than notified: an error written straight out of a command
-- lands on a hit-enter prompt, and a session stopped for one is indistinguishable, from the
-- game, from a session that has hung.
local function complain(message)
  vim.schedule(function()
    vim.api.nvim_echo({ { message, 'ErrorMsg' } }, false, {})
  end)
end

local function close(bang)
  local closing = vim.api.nvim_get_current_buf()

  -- A tree, a terminal, a help window: not a file, so :q on it means the window, which is
  -- also the only way to shut a sidebar.
  if vim.bo[closing].buftype ~= '' or not vim.bo[closing].buflisted then
    vim.cmd(bang and 'quit!' or 'quit')
    return
  end

  if vim.bo[closing].modified and not bang then
    complain('E37: No write since last change (add ! to override)')
    return
  end

  local showing = replacement(closing)

  for _, window in ipairs(vim.api.nvim_list_wins()) do
    if vim.api.nvim_win_get_buf(window) == closing then
      vim.api.nvim_win_set_buf(window, showing)
    end
  end

  -- Said out loud rather than swallowed. The windows have already moved on by here, so a
  -- delete that quietly fails looks exactly like "it went to an empty buffer and the tab is
  -- still there" - which is a report nobody can act on.
  local deleted, reason = pcall(vim.api.nvim_buf_delete, closing, { force = bang })
  if not deleted then
    complain('UwUTerm: could not close the buffer - ' .. tostring(reason))
  end
end

vim.api.nvim_create_user_command('UwuQuit', function(opts) close(opts.bang) end, { bang = true })

-- Fires when the whole command line is "q", which is when it is a quit and not part of
-- something longer. Typing ! next expands it too, so :q! arrives as :UwuQuit!.
vim.cmd([[cnoreabbrev <expr> q (getcmdtype() ==# ':' && getcmdline() ==# 'q') ? 'UwuQuit' : 'q']])

-- A session outlives edits to this file, so the one running is not necessarily the one on
-- disk. That is worth being able to check in a command rather than by guessing at behaviour.
vim.api.nvim_create_user_command('UwuDiag', function()
  vim.api.nvim_echo({ { table.concat({
    'loaded  ' .. source,
    'written ' .. os.date('%Y-%m-%d %H:%M:%S', vim.fn.getftime(source)),
    'started ' .. vim.g.uwuterm_daemon_started,
    ':q      ' .. (vim.fn.exists(':UwuQuit') == 2 and 'closes the buffer' or 'NOT IN FORCE'),
  }, '\n') } }, false, {})
end, {})

vim.g.uwuterm_daemon_started = os.date('%Y-%m-%d %H:%M:%S')

-- A backstop for the ways out that are not :q at all - ZZ, a plugin closing its own last
-- window, a sidebar that shuts when nothing else is left. QuitPre cannot cancel a quit, but
-- one more window means the quit closes a window rather than the session.
vim.api.nvim_create_autocmd('QuitPre', {
  group = vim.api.nvim_create_augroup('uwuterm_daemon', { clear = true }),
  callback = function()
    if #vim.api.nvim_list_tabpages() > 1 then return end

    local ordinary = vim.tbl_filter(function(window)
      return vim.api.nvim_win_get_config(window).relative == ''
    end, vim.api.nvim_tabpage_list_wins(0))

    if #ordinary > 1 then return end

    local leaving = vim.api.nvim_get_current_win()
    vim.cmd('botright new')

    -- Unlisted and wiped when it is left, so this cannot silt the session up with a blank
    -- buffer per quit.
    vim.bo.buflisted = false
    vim.bo.bufhidden = 'wipe'

    vim.api.nvim_set_current_win(leaving)
  end,
})
