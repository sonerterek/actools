using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AcManager.UiObserver
{
    #region Data Models

    /// <summary>
    /// Represents a StreamDeck key definition with title and icon.
    /// </summary>
    public class SDPKeyDef
    {
        public string KeyName { get; set; }
        public string Title { get; set; }
        public string IconSpec { get; set; }

        public SDPKeyDef(string keyName, string title = null, string iconSpec = null)
        {
            KeyName = keyName;
            Title = title;
            IconSpec = iconSpec;
        }

        public string ToCommand()
        {
            var parts = new List<string> { KeyName };

            if (!string.IsNullOrEmpty(Title))
            {
                parts.Add($"\"{Title}\"");
            }
            else if (IconSpec != null)
            {
                parts.Add("null");
            }

            if (!string.IsNullOrEmpty(IconSpec))
            {
                parts.Add(IconSpec);
            }

            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Represents a StreamDeck page with a 5x3 grid layout.
    /// </summary>
    public class SDPPageDef
    {
        public string PageName { get; set; }
        public string[][] KeyGrid { get; set; }

        public SDPPageDef(string pageName)
        {
            PageName = pageName;
            KeyGrid = new string[5][];
            for (int i = 0; i < 5; i++)
            {
                KeyGrid[i] = new string[3];
            }
        }

        public void SetKey(int row, int col, string keyName)
        {
            if (row < 0 || row >= 5 || col < 0 || col >= 3)
            {
                throw new ArgumentOutOfRangeException($"Invalid position: [{row},{col}]. Must be [0-4, 0-2]");
            }
            KeyGrid[row][col] = keyName;
        }

        public string GetKey(int row, int col)
        {
            if (row < 0 || row >= 5 || col < 0 || col >= 3)
            {
                return null;
            }
            return KeyGrid[row][col];
        }

        public string ToCommand()
        {
            var jsonGrid = JsonConvert.SerializeObject(KeyGrid);
            return $"{PageName} {jsonGrid}";
        }
    }

    /// <summary>
    /// Event arguments for StreamDeck key press events.
    /// </summary>
    public class SDPKeyPressEventArgs : EventArgs
    {
        public string KeyName { get; set; }
        public DateTime Timestamp { get; set; }

        public SDPKeyPressEventArgs(string keyName)
        {
            KeyName = keyName;
            Timestamp = DateTime.Now;
        }
    }

    #endregion

    #region Client Implementation

    /// <summary>
    /// Client for communicating with the NWRS AC StreamDeck Plugin via Named Pipe.
    /// 
    /// NEW ARCHITECTURE (State Replication):
    /// - Fire-and-forget API: DefineKey(), DefinePage(), SwitchPage() return void
    /// - Maintains authoritative state internally
    /// - Automatically replicates state to plugin on connection
    /// - Auto-reconnects and restores state on connection loss
    /// - No error handling needed by caller - all errors logged internally
    /// 
    /// Protocol:
    /// - Named Pipe: "NWRS_AC_SDPlugin_Pipe"
    /// - Commands: DefineKey, DefinePage, SwitchPage
    /// - Events: KeyPress
    /// </summary>
    public class SDPClient : IDisposable
    {
        #region Constants

        private const string PipeName = "NWRS_AC_SDPlugin_Pipe";
        private const int ConnectionTimeoutMs = 2000;
        private const int WatchdogIntervalMs = 5000;

        #endregion

        #region Fields

        private NamedPipeClientStream _pipe;
        private StreamWriter _writer;
        private StreamReader _reader;
        private CancellationTokenSource _listenerCts;
        private Task _listenerTask;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private readonly object _writeLock = new object();

        // ???????????????????????????????????????????????????????????????
        // AUTHORITATIVE STATE (what SHOULD be on the plugin)
        // ???????????????????????????????????????????????????????????????

        private readonly Dictionary<string, SDPKeyDef> _keys = new Dictionary<string, SDPKeyDef>();
        private readonly Dictionary<string, SDPPageDef> _pages = new Dictionary<string, SDPPageDef>();
        private string _currentPage = null;

        // ???????????????????????????????????????????????????????????????
        // REPLICATION STATE
        // ???????????????????????????????????????????????????????????????

        private bool _isReplicaSynced = false;
        private bool _isReplicating = false;
        private readonly Queue<string> _asyncCommandQueue = new Queue<string>();

        // ???????????????????????????????????????????????????????????????
        // CONNECTION STATE & MONITORING
        // ???????????????????????????????????????????????????????????????

        private System.Threading.Timer _connectionWatchdog;
        private bool _isReconnecting = false;

        #endregion

        #region Events

        public event EventHandler<SDPKeyPressEventArgs> KeyPressed;
        public event EventHandler Connected;
        public event EventHandler Disconnected;

        #endregion

        #region Properties

        public bool IsConnected => _pipe?.IsConnected == true;
        public int KeyCount => _keys.Count;
        public int PageCount => _pages.Count;
        public bool SDPVerboseDebug { get; set; } = false;

        #endregion

        #region Constructor

        public SDPClient()
        {
            // ? FIX: Start watchdog immediately on construction
            // This ensures reconnection works even if initial connection fails
            StartConnectionWatchdog();
        }

        #endregion

        #region Connection Management

        /// <summary>
        /// Connects to the StreamDeck plugin's named pipe server.
        /// Automatically starts state replication in background.
        /// </summary>
        public async Task ConnectAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (_disposed) return;

                // Disconnect if already connected
                if (_pipe != null && _pipe.IsConnected)
                {
                    Debug.WriteLine("[SDPClient] Already connected, disconnecting first...");
                    DisconnectInternal();
                }

                Debug.WriteLine("[SDPClient] Disconnecting...");
                DisconnectInternal();

                Debug.WriteLine($"[SDPClient] Connecting to pipe: {PipeName}");
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                // .NET 4.5.2 doesn't have ConnectAsync - use Task.Run with synchronous Connect
                var connectTask = Task.Run(() => _pipe.Connect(ConnectionTimeoutMs));
                await connectTask;

                if (!_pipe.IsConnected)
                {
                    Debug.WriteLine("[SDPClient] Connection failed");
                    DisconnectInternal();
                    return;
                }

                _writer = new StreamWriter(_pipe) { AutoFlush = true };
                _reader = new StreamReader(_pipe);

                // Start listener task
                _listenerCts = new CancellationTokenSource();
                _listenerTask = Task.Run(() => ListenForEventsAsync(_listenerCts.Token));

                Debug.WriteLine("[SDPClient] Connected successfully");
                OnConnected();

                // ? FIX: Replicate state immediately after initial connection
                await ReplicateStateToPluginAsync();

                // Start watchdog
                StartConnectionWatchdog();
            }
            catch (TimeoutException)
            {
                Debug.WriteLine("[SDPClient] Connect() failed: The operation has timed out.");
                DisconnectInternal();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Connect() failed: {ex.Message}");
                DisconnectInternal();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private void StartConnectionWatchdog()
        {
            _connectionWatchdog?.Dispose();

            _connectionWatchdog = new System.Threading.Timer(
                callback: async _ => await CheckConnectionHealthAsync(),
                state: null,
                dueTime: WatchdogIntervalMs,
                period: WatchdogIntervalMs
            );

            if (SDPVerboseDebug) Debug.WriteLine("[SDPClient] Watchdog started");
        }

        private async Task CheckConnectionHealthAsync()
        {
            if (_isReconnecting) return;

            if (!IsConnected)
            {
                if (SDPVerboseDebug) Debug.WriteLine("[SDPClient] Watchdog detected disconnection");
                await TryReconnectAsync();
            }
        }

        public void Disconnect()
        {
            _connectionLock.Wait();
            try
            {
                DisconnectInternal();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private void DisconnectInternal()
        {
            Debug.WriteLine("[SDPClient] Disconnecting...");

            try
            {
                _listenerCts?.Cancel();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Error cancelling listener: {ex.Message}");
            }

            try
            {
                _listenerTask?.Wait(500);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Error waiting for listener: {ex.Message}");
            }

            try
            {
                _listenerCts?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Error disposing listener CTS: {ex.Message}");
            }

            _listenerCts = null;
            _listenerTask = null;

            _isReplicaSynced = false;

            lock (_asyncCommandQueue)
            {
                var discardedCount = _asyncCommandQueue.Count;
                _asyncCommandQueue.Clear();

                if (discardedCount > 0)
                {
                    Debug.WriteLine($"[SDPClient] Discarded {discardedCount} queued commands");
                }
            }

            // ? FIX: Dispose resources with proper error handling
            if (_writer != null)
            {
                try
                {
                    _writer.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed - ignore
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SDPClient] Error disposing writer: {ex.Message}");
                }
                _writer = null;
            }

            if (_reader != null)
            {
                try
                {
                    _reader.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed - ignore
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SDPClient] Error disposing reader: {ex.Message}");
                }
                _reader = null;
            }

            if (_pipe != null)
            {
                try
                {
                    _pipe.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed - ignore
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SDPClient] Error disposing pipe: {ex.Message}");
                }
                _pipe = null;
            }

            Debug.WriteLine("[SDPClient] Disconnected");
            
            try
            {
                OnDisconnected();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Error raising Disconnected event: {ex.Message}");
            }
        }

        #endregion

        #region Fire-and-Forget API

        /// <summary>
        /// Defines a key (fire-and-forget, synchronous).
        /// Updates authoritative state and queues for sending.
        /// </summary>
        public void DefineKey(string keyName, string title = null, string iconSpec = null)
        {
            if (string.IsNullOrWhiteSpace(keyName))
            {
                LogError($"DefineKey failed: keyName cannot be empty");
                return;
            }

            var keyDef = new SDPKeyDef(keyName, title, iconSpec);

            lock (_keys)
            {
                _keys[keyName] = keyDef;
            }

            if (_isReplicaSynced)
            {
                QueueAsyncCommand($"DefineKey {keyDef.ToCommand()}");
            }

            if (SDPVerboseDebug)
            {
                Debug.WriteLine($"[SDPClient] Key '{keyName}' defined in state");
            }
        }

        /// <summary>
        /// Defines a page (fire-and-forget, synchronous).
        /// Updates authoritative state and queues for sending.
        /// </summary>
        public void DefinePage(string pageName, string[][] keyGrid)
        {
            if (string.IsNullOrWhiteSpace(pageName))
            {
                LogError($"DefinePage failed: pageName cannot be empty");
                return;
            }

            if (keyGrid == null || keyGrid.Length != 5)
            {
                LogError($"DefinePage failed: keyGrid must have 5 rows");
                return;
            }

            for (int i = 0; i < 5; i++)
            {
                if (keyGrid[i] == null || keyGrid[i].Length != 3)
                {
                    LogError($"DefinePage failed: row {i} must have 3 columns");
                    return;
                }
            }

            var pageDef = new SDPPageDef(pageName) { KeyGrid = keyGrid };

            lock (_pages)
            {
                _pages[pageName] = pageDef;
            }

            if (_isReplicaSynced)
            {
                QueueAsyncCommand($"DefinePage {pageDef.ToCommand()}");
            }

            if (SDPVerboseDebug)
            {
                Debug.WriteLine($"[SDPClient] Page '{pageName}' defined in state");
            }
        }

        /// <summary>
        /// Switches to the specified page (fire-and-forget, synchronous).
        /// Updates authoritative state and queues for sending.
        /// </summary>
        public void SwitchPage(string pageName)
        {
            if (string.IsNullOrWhiteSpace(pageName))
            {
                LogError($"SwitchPage failed: pageName cannot be empty");
                return;
            }

            _currentPage = pageName;

            if (_isReplicaSynced)
            {
                QueueAsyncCommand($"SwitchPage {pageName}");
            }

            if (SDPVerboseDebug)
            {
                Debug.WriteLine($"[SDPClient] Current page set to '{pageName}'");
            }
        }

        #endregion

        #region State Replication

        /// <summary>
        /// Replicates the entire authoritative state to the plugin in dependency order.
        /// Called automatically after connection is established.
        /// </summary>
        private async Task ReplicateStateToPluginAsync()
        {
            if (_isReplicating)
            {
                Debug.WriteLine("[SDPClient] Replication already in progress");
                return;
            }

            _isReplicating = true;
            _isReplicaSynced = false;

            try
            {
                int keyCount, pageCount;
                lock (_keys) { keyCount = _keys.Count; }
                lock (_pages) { pageCount = _pages.Count; }

                Debug.WriteLine($"[SDPClient] Replicating state: {keyCount} keys, {pageCount} pages");

                // Phase 1: Send all keys
                List<SDPKeyDef> keysList;
                lock (_keys)
                {
                    keysList = _keys.Values.ToList();
                }

                foreach (var keyDef in keysList)
                {
                    if (!IsConnected)
                    {
                        Debug.WriteLine("[SDPClient] Disconnected during key replication");
                        return;
                    }

                    SendCommandImmediate($"DefineKey {keyDef.ToCommand()}");
                    await Task.Delay(10);
                }

                Debug.WriteLine($"[SDPClient] ? Replicated {keysList.Count} keys");

                // Phase 2: Send all pages in dependency order (base pages first, then derived)
                List<SDPPageDef> pagesList;
                lock (_pages)
                {
                    pagesList = _pages.Values.ToList();
                }

                // ? FIX: Order pages so base pages are sent before derived pages
                var orderedPages = OrderPagesByDependency(pagesList);

                foreach (var pageDef in orderedPages)
                {
                    if (!IsConnected)
                    {
                        Debug.WriteLine("[SDPClient] Disconnected during page replication");
                        return;
                    }

                    SendCommandImmediate($"DefinePage {pageDef.ToCommand()}");
                    await Task.Delay(10);
                }

                Debug.WriteLine($"[SDPClient] ? Replicated {orderedPages.Count} pages");

                // Phase 3: Switch to current page (if set)
                if (!string.IsNullOrEmpty(_currentPage))
                {
                    if (!IsConnected)
                    {
                        Debug.WriteLine("[SDPClient] Disconnected before page switch");
                        return;
                    }

                    Debug.WriteLine($"[SDPClient] Restoring current page: {_currentPage}");
                    SendCommandImmediate($"SwitchPage {_currentPage}");
                    Debug.WriteLine($"[SDPClient] ? Switched to page: {_currentPage}");
                }
                else
                {
                    Debug.WriteLine("[SDPClient] No current page to restore");
                }

                _isReplicaSynced = true;
                Debug.WriteLine("[SDPClient] ? Replication complete");

                ProcessAsyncCommandQueue();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Replication failed: {ex.Message}");
                LogError($"Replication failed: {ex.Message}");
                _isReplicaSynced = false;
            }
            finally
            {
                _isReplicating = false;
            }
        }

        /// <summary>
        /// Orders pages so base pages are sent before derived pages.
        /// Pages with inheritance (e.g., "ChildPage:BasePage") must come after their base.
        /// </summary>
        private List<SDPPageDef> OrderPagesByDependency(List<SDPPageDef> pages)
        {
            var result = new List<SDPPageDef>();
            var remaining = new List<SDPPageDef>(pages);
            var addedNames = new HashSet<string>();

            // Keep processing until all pages are added
            while (remaining.Count > 0)
            {
                bool addedAny = false;

                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var page = remaining[i];
                    var basePage = GetBasePageName(page.PageName);

                    // Can add if:
                    // 1. No base page (independent page), OR
                    // 2. Base page already added
                    if (basePage == null || addedNames.Contains(basePage))
                    {
                        result.Add(page);
                        addedNames.Add(page.PageName);
                        remaining.RemoveAt(i);
                        addedAny = true;

                        if (SDPVerboseDebug && basePage != null)
                        {
                            Debug.WriteLine($"[SDPClient] Page ordering: {page.PageName} (depends on {basePage})");
                        }
                    }
                }

                // If we couldn't add anything, we have circular dependency or missing base
                if (!addedAny)
                {
                    Debug.WriteLine($"[SDPClient] WARNING: Circular dependency or missing base pages detected");
                    
                    // Add remaining pages anyway (will fail on plugin side, but at least we tried)
                    foreach (var page in remaining)
                    {
                        Debug.WriteLine($"[SDPClient] WARNING: Page '{page.PageName}' has unresolved base page");
                        result.Add(page);
                    }
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Extracts base page name from page name if it uses inheritance syntax (PageName:BasePage).
        /// Returns null if page doesn't use inheritance.
        /// </summary>
        private string GetBasePageName(string pageName)
        {
            if (string.IsNullOrEmpty(pageName))
                return null;

            var colonIndex = pageName.IndexOf(':');
            if (colonIndex > 0 && colonIndex < pageName.Length - 1)
            {
                return pageName.Substring(colonIndex + 1);
            }

            return null;
        }

        private void QueueAsyncCommand(string command)
        {
            lock (_asyncCommandQueue)
            {
                _asyncCommandQueue.Enqueue(command);

                if (SDPVerboseDebug)
                {
                    Debug.WriteLine($"[SDPClient] Queued: {command}");
                }
            }

            Task.Run(() => ProcessAsyncCommandQueue());
        }

        private void ProcessAsyncCommandQueue()
        {
            lock (_asyncCommandQueue)
            {
                if (!IsConnected || !_isReplicaSynced)
                {
                    return;
                }

                while (_asyncCommandQueue.Count > 0)
                {
                    var command = _asyncCommandQueue.Dequeue();
                    SendCommandImmediate(command);
                }
            }
        }

        private void SendCommandImmediate(string command)
        {
            if (!IsConnected)
            {
                LogError($"Cannot send (not connected): {command}");
                return;
            }

            try
            {
                lock (_writeLock)
                {
                    _writer.WriteLine(command);

                    if (SDPVerboseDebug)
                    {
                        Debug.WriteLine($"[SDPClient] Sent: {command}");
                    }
                }
            }
            catch (IOException ex)
            {
                LogError($"Send failed: {command} - {ex.Message}");

                Task.Run(() =>
                {
                    DisconnectInternal();
                    Task.Run(async () => await TryReconnectAsync());
                });
            }
            catch (Exception ex)
            {
                LogError($"Send exception: {command} - {ex.Message}");
            }
        }

        #endregion

        #region Reconnection

        private async Task TryReconnectAsync()
        {
            if (_isReconnecting || _disposed) return;

            _isReconnecting = true;
            Debug.WriteLine("[SDPClient] Starting reconnection...");

            // Exponential backoff capped at 5 seconds: 1s, 2s, 5s, 5s, 5s...
            int[] retryDelays = { 1000, 2000, 5000 };
            int retryIndex = 0;

            while (_isReconnecting && !_disposed)
            {
                // Use max delay of 5s for all attempts after the third
                int delayMs = retryDelays[Math.Min(retryIndex, retryDelays.Length - 1)];
                Debug.WriteLine($"[SDPClient] Reconnecting in {delayMs}ms...");

                await Task.Delay(delayMs);

                if (_disposed || !_isReconnecting) break;

                try
                {
                    Debug.WriteLine("[SDPClient] Disconnecting...");
                    DisconnectInternal();

                    Debug.WriteLine("[SDPClient] Disconnecting...");
                    DisconnectInternal();

                    Debug.WriteLine($"[SDPClient] Connecting to pipe: {PipeName}");

                    // Attempt to connect (fire-and-forget, no result check)
                    await ConnectAsync();

                    // Check if connection succeeded
                    if (_pipe != null && _pipe.IsConnected)
                    {
                        Debug.WriteLine("[SDPClient] ? Reconnected");

                        // Replicate state to plugin
                        await ReplicateStateToPluginAsync();

                        _isReconnecting = false;
                        return;
                    }

                    Debug.WriteLine("[SDPClient] Reconnection attempt failed");
                    retryIndex++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SDPClient] Reconnection error: {ex.Message}");
                    retryIndex++;
                }
            }

            _isReconnecting = false;
        }

        #endregion

        #region Error Logging

        private void LogError(string message)
        {
            Debug.WriteLine($"[SDPClient] ERROR: {message}");

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var logFile = System.IO.Path.Combine(appData, "AcTools Content Manager", "SDPClient Errors.log");

                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logFile));

                var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] {message}\r\n";
                System.IO.File.AppendAllText(logFile, entry);
            }
            catch
            {
                // Silent failure
            }
        }

        #endregion

        #region Event Listening

        private async Task ListenForEventsAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine("[SDPClient] Listener started");

            try
            {
                while (!cancellationToken.IsCancellationRequested && _pipe?.IsConnected == true)
                {
                    try
                    {
                        var line = await _reader.ReadLineAsync();

                        if (line == null)
                        {
                            Debug.WriteLine("[SDPClient] Plugin disconnected");
                            break;
                        }

                        ProcessEvent(line);
                    }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"[SDPClient] Connection lost: {ex.Message}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SDPClient] Event error: {ex.Message}");
                    }
                }
            }
            finally
            {
                Debug.WriteLine("[SDPClient] Listener stopped");
                if (_pipe?.IsConnected == true)
                {
                    Task.Run(() => Disconnect());
                }
            }
        }

        private void ProcessEvent(String message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            if (message.Length > 0 && message[0] == '\uFEFF')
            {
                message = message.Substring(1);
            }

            if (SDPVerboseDebug) Debug.WriteLine($"[SDPClient] Received: {message}");

            var parts = message.Split(new[] { ' ' }, 2);
            if (parts.Length < 1) return;

            var eventType = parts[0];

            if (eventType == "KeyPress" && parts.Length >= 2)
            {
                OnKeyPressed(parts[1]);
            }
            else if (SDPVerboseDebug)
            {
                Debug.WriteLine($"[SDPClient] Unknown event: {eventType}");
            }
        }

        #endregion

        #region Event Handlers

        protected virtual void OnConnected()
        {
            Connected?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnDisconnected()
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnKeyPressed(string keyName)
        {
            Debug.WriteLine($"[SDPClient] OnKeyPressed: Raising KeyPressed event for '{keyName}'");
            Debug.WriteLine($"[SDPClient] OnKeyPressed: Event has {KeyPressed?.GetInvocationList().Length ?? 0} subscribers");
            
            try
            {
                KeyPressed?.Invoke(this, new SDPKeyPressEventArgs(keyName));
                Debug.WriteLine($"[SDPClient] OnKeyPressed: Event raised successfully for '{keyName}'");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] OnKeyPressed: Exception during event raise: {ex.Message}");
                Debug.WriteLine($"[SDPClient] OnKeyPressed: Stack trace: {ex.StackTrace}");
            }
        }

        #endregion

        #region Utility Methods

        public bool IsKeyDefined(string keyName)
        {
            lock (_keys)
            {
                return _keys.ContainsKey(keyName);
            }
        }

        public bool IsPageDefined(string pageName)
        {
            lock (_pages)
            {
                return _pages.ContainsKey(pageName);
            }
        }

        public IEnumerable<string> GetDefinedKeyNames()
        {
            lock (_keys)
            {
                return _keys.Keys.ToList();
            }
        }

        public IEnumerable<string> GetDefinedPageNames()
        {
            lock (_pages)
            {
                return _pages.Keys.ToList();
            }
        }

        #endregion

        #region IDisposable

        private bool _disposed = false;

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _connectionWatchdog?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Error disposing watchdog: {ex.Message}");
            }
            _connectionWatchdog = null;

            Disconnect();
            
            try
            {
                _connectionLock?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPClient] Error disposing connection lock: {ex.Message}");
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~SDPClient()
        {
            try
            {
                Dispose();
            }
            catch (Exception ex)
            {
                // Suppress all exceptions during finalization
                // Finalizers should never throw exceptions
                Debug.WriteLine($"[SDPClient] Exception in finalizer (suppressed): {ex.Message}");
            }
        }

        #endregion
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// Helper for discovering PNG icon files in the SDIcons directory.
    /// </summary>
    public static class SDPIconHelper
    {
        public static string GetIconsDirectory()
        {
            var assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            return Path.Combine(assetsPath, "SDIcons");
        }

        public static Dictionary<string, string> DiscoverIcons()
        {
            var iconsDir = GetIconsDirectory();
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(iconsDir))
            {
                Debug.WriteLine($"[SDPIconHelper] Directory not found: {iconsDir}");
                return icons;
            }

            try
            {
                var pngFiles = Directory.GetFiles(iconsDir, "*.png", SearchOption.TopDirectoryOnly);
                foreach (var file in pngFiles)
                {
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
                    icons[nameWithoutExt] = file;
                }

                Debug.WriteLine($"[SDPIconHelper] Discovered {icons.Count} icons");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SDPIconHelper] Error: {ex.Message}");
            }

            return icons;
        }

        public static string GetIconPath(string iconName)
        {
            if (string.IsNullOrWhiteSpace(iconName)) return null;

            var iconsDir = GetIconsDirectory();
            var iconPath = Path.Combine(iconsDir, iconName + ".png");

            return File.Exists(iconPath) ? iconPath : null;
        }

        public static string CreateTextIcon(string text)
        {
            return $"!{text}";
        }

        public static bool IconExists(string iconNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(iconNameOrPath)) return false;

            if (iconNameOrPath.StartsWith("!")) return true;
            if (iconNameOrPath.StartsWith("data:image/")) return true;
            if (File.Exists(iconNameOrPath)) return true;

            return GetIconPath(iconNameOrPath) != null;
        }
    }

    #endregion
}
