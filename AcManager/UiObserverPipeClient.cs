using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AcManager.Tools.Helpers {
    /// <summary>
    /// Lightweight, standalone named-pipe client that listens for JSON messages (one JSON object per line)
    /// and raises events mapped from the message's "Type" property.
    /// Designed to be integrated into another process.
    /// </summary>
    public sealed class UiObserverPipeClient : IDisposable {
        private readonly string _pipeName;
        private readonly SynchronizationContext _syncContext;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _listenTask;

        public UiObserverPipeClient(string pipeName, SynchronizationContext syncContext = null) {
            if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentNullException(nameof(pipeName));
            _pipeName = pipeName;
            _syncContext = syncContext;
        }

        /// <summary>
        /// Raised for every received event. Payload contains parsed JSON object.
        /// </summary>
        public event EventHandler<UiEventArgs> EventReceived;

        // Convenience events for common types (type names are taken from the sender side).
        public event EventHandler<UiEventArgs> WindowCreated;
        public event EventHandler<UiEventArgs> WindowLoaded;
        public event EventHandler<UiEventArgs> WindowClosed;
        public event EventHandler<UiEventArgs> WindowActivated;
        public event EventHandler<UiEventArgs> WindowDeactivated;

        public event EventHandler<UiEventArgs> ControlCreated;
        public event EventHandler<UiEventArgs> ControlDestroyed;
        public event EventHandler<UiEventArgs> ControlGotFocus;
        public event EventHandler<UiEventArgs> ControlLostFocus;
        public event EventHandler<UiEventArgs> ButtonClick;
        public event EventHandler<UiEventArgs> SelectionChanged;
        public event EventHandler<UiEventArgs> ValueChanged;
        public event EventHandler<UiEventArgs> TextChanged;

        public event EventHandler<UiEventArgs> PopupOpened;
        public event EventHandler<UiEventArgs> PopupClosed;

        public event EventHandler<UiEventArgs> FrameNavigated;

        /// <summary>
        /// Starts background listening (no-op if already started).
        /// </summary>
        public void Start() {
            if (_listenTask != null) return;
            _listenTask = Task.Run(ListenLoopAsync, CancellationToken.None);
        }

        private async Task ListenLoopAsync() {
            while (!_cts.IsCancellationRequested) {
                try {
                    using (var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.In, PipeOptions.Asynchronous)) {
                        try {
                            // attempt connect with timeout
                            var connectTask = Task.Run(() => client.Connect(2000), _cts.Token);
                            await connectTask.ConfigureAwait(false);
                        } catch (OperationCanceledException) {
                            return;
                        } catch {
                            // failed to connect — retry after delay
                            await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                            continue;
                        }

                        using (var reader = new StreamReader(client)) {
                            while (!_cts.IsCancellationRequested && client.IsConnected) {
                                string line;
                                try {
                                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                                } catch {
                                    break;
                                }

                                if (line == null) break; // pipe closed

                                // parse JSON and dispatch
                                JObject json = null;
                                try {
                                    json = JObject.Parse(line);
                                } catch {
                                    // malformed JSON — ignore
                                }

                                Dispatch(json);
                            }
                        }
                    }
                } catch (OperationCanceledException) {
                    break;
                } catch {
                    // Unexpected error — wait and retry
                    try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        private void Dispatch(JObject json) {
            var args = new UiEventArgs(json);
            Post(() => EventReceived?.Invoke(this, args));

            var type = json?["Type"]?.ToString();
            if (string.IsNullOrEmpty(type)) return;

            // normalize
            switch (type) {
                case "WindowCreated":
                    Post(() => WindowCreated?.Invoke(this, args)); break;
                case "WindowLoaded":
                    Post(() => WindowLoaded?.Invoke(this, args)); break;
                case "WindowClosed":
                    Post(() => WindowClosed?.Invoke(this, args)); break;
                case "WindowActivated":
                    Post(() => WindowActivated?.Invoke(this, args)); break;
                case "WindowDeactivated":
                    Post(() => WindowDeactivated?.Invoke(this, args)); break;

                case "ControlCreated":
                    Post(() => ControlCreated?.Invoke(this, args)); break;
                case "ControlDestroyed":
                    Post(() => ControlDestroyed?.Invoke(this, args)); break;

                case "ControlGotFocus":
                    Post(() => ControlGotFocus?.Invoke(this, args)); break;
                case "ControlLostFocus":
                    Post(() => ControlLostFocus?.Invoke(this, args)); break;
                case "ButtonClick":
                    Post(() => ButtonClick?.Invoke(this, args)); break;
                case "SelectionChanged":
                    Post(() => SelectionChanged?.Invoke(this, args)); break;
                case "ValueChanged":
                    Post(() => ValueChanged?.Invoke(this, args)); break;
                case "TextChanged":
                    Post(() => TextChanged?.Invoke(this, args)); break;

                case "PopupOpened":
                    Post(() => PopupOpened?.Invoke(this, args)); break;
                case "PopupClosed":
                    Post(() => PopupClosed?.Invoke(this, args)); break;

                case "FrameNavigated":
                    Post(() => FrameNavigated?.Invoke(this, args)); break;

                default:
                    // unknown type — only EventReceived will be raised
                    break;
            }
        }

        private void Post(Action a) {
            if (a == null) return;
            if (_syncContext != null) {
                _syncContext.Post(_ => {
                    try { a(); } catch { /* swallow */ }
                }, null);
            } else {
                try { a(); } catch { /* swallow */ }
            }
        }

        public void Stop() {
            _cts.Cancel();
            try { _listenTask?.Wait(500); } catch { }
            _listenTask = null;
        }

        public void Dispose() {
            Stop();
            _cts.Dispose();
        }
    }

    public sealed class UiEventArgs : EventArgs {
        public UiEventArgs(JObject payload) {
            Payload = payload;
            Type = payload?["Type"]?.ToString();
        }

        /// <summary>
        /// Raw parsed JSON payload (may be null if parsing failed).
        /// </summary>
        public JObject Payload { get; }

        /// <summary>
        /// Value of top-level 'Type' property when present.
        /// </summary>
        public string Type { get; }
    }
}