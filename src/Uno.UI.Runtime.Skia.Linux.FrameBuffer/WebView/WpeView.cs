// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Uno.UI.Runtime.Skia.Wpe;

/// <summary>
/// One WPE WebKit view without XAML: the web view and its fdo exportable (frames as EGLImages), commands, input dispatch,
/// text input and frame import. Commands and input are posted to the GLib thread; the events are raised there.
/// The frame import runs on the host's render thread (<see cref="FrameBufferGpu.TryInvoke"/>): Skia's canvas only records.
/// </summary>
internal sealed unsafe class WpeView
{
	private const uint ActivityVisible = 1, ActivityFocused = 2, ActivityInWindow = 4;
	private const uint GlTexture2D = 0x0DE1;

	private delegate void GlEglImageTarget(uint target, IntPtr image);

	private static readonly WpeNative.AsyncReady s_scriptDone = OnScriptDone;
	private static readonly WpeNative.DestroyNotify s_backendDestroyed = OnBackendDestroyed;
	private static long s_frames;

	/// <summary>Frames exported by all views so far (diagnostics).</summary>
	public static long TotalFrames => Interlocked.Read(ref s_frames);


	// delegates and the client struct stay alive as long as the view
	private readonly WpeNative.ExportImage _onFdoImage, _onEglImage, _onShm;
	private readonly WpeNative.LoadChanged _onLoadChanged;
	private readonly WpeNative.LoadFailed _onLoadFailed;
	private readonly WpeNative.NotifyProperty _onTitle;
	private readonly object _gate = new();
	private readonly Dictionary<int, WpeNative.TouchRaw> _touches = new();
	// touch moves are coalesced (GLib thread), see QueueMove
	private const long TouchMoveIntervalMs = 33;
	private readonly Dictionary<int, WpeNative.TouchRaw> _pendingMoves = new();
	private long _lastMoveSent;
	private bool _moveFlushQueued;
	private IntPtr _client, _exportable, _backend, _view;
	private WpeImeContext? _ime;
	private IntPtr _pending, _current; // wpe_fdo_egl_exported_image*
	private uint _texture;
	private SKImage? _image;
	private GlEglImageTarget? _bindImage;
	private volatile bool _disposed;
	private uint _width = 800, _height = 600;
	private float _scale = 1;
	private bool _visible = true;
	private bool _failed;
	private bool _started, _created;
	private string? _initialUri;
	private readonly List<Action> _beforeCreate = new(); // commands posted before the view exists, in order
	private GCHandle _self; // native callbacks reach this object until WebKit has destroyed the view backend
	private ulong[] _signals = Array.Empty<ulong>();

	/// <summary>Render thread: a new frame was imported (the canvas redraws).</summary>
	public event Action? FrameReady;

	/// <summary>GLib thread: a navigation started (uri).</summary>
	public event Action<string>? NavigationStarted;

	/// <summary>GLib thread: a navigation finished (uri, success, canGoBack, canGoForward).</summary>
	public event Action<string, bool, bool, bool>? NavigationFinished;

	/// <summary>GLib thread: the document title changed.</summary>
	public event Action? TitleChanged;

	/// <summary>GLib thread: an editable element gained or lost focus (focused, WebKitInputPurpose, WebKitInputHints).</summary>
	public event Action<bool, int, int>? TextInputFocus;

	/// <summary>GLib thread: text of the focused editable element. Never log it.</summary>
	public event Action<string>? TextInputSurrounding;

	/// <summary>The latest frame (render thread), or null before the first one.</summary>
	public SKImage? Image => Volatile.Read(ref _image);

	/// <summary>Title of the document, kept current on the GLib thread.</summary>
	public string Title { get; private set; } = string.Empty;

	/// <summary>Why the view cannot show anything, or null.</summary>
	public string? Failure
	{
		get => _failure;
		private set
		{
			if (value is not null && _failure is null)
			{
				WpeRuntime.Log("view failed: " + value);
			}
			_failure = value;
		}
	}

	private string? _failure;
	private bool _firstFrameLogged;
	private bool _inputLogged;

	/// <summary><see cref="Environment.TickCount64"/> of the last press or release on this view (0: none yet).</summary>
	public long LastUserInput { get; private set; }

	public WpeView(uint width, uint height, float scale, string? initialUri)
	{
		_width = Math.Max(1, width);
		_height = Math.Max(1, height);
		_scale = scale;
		_onFdoImage = (_, image) => OnFrame(image);
		_onEglImage = (_, _) => { };
		_onShm = (_, buffer) => WpeNative.wpe_view_backend_exportable_fdo_egl_dispatch_release_shm_exported_buffer(_exportable, buffer);
		_onLoadChanged = OnLoadChanged;
		_onLoadFailed = OnLoadFailed;
		_onTitle = (_, _, _) => OnTitle();
		_self = GCHandle.Alloc(this);
		_initialUri = initialUri;
	}

	/// <summary>
	/// Creates the WebKit view, once, with its real size: the first <see cref="SetSize"/> calls it. A view created with a
	/// placeholder size and resized afterwards kept the placeholder layout size after a reload.
	/// </summary>
	private void Start()
	{
		_started = true;
		var uri = _initialUri;
		WpeRuntime.Run(() => Create(uri));
	}

	// ---- GLib thread -------------------------------------------------------------------------------------------------

	private void Create(string? initialUri)
	{
		if (_disposed)
		{
			return;
		}
		try
		{
			_client = Marshal.AllocHGlobal(sizeof(WpeNative.EglClient));
			*(WpeNative.EglClient*)_client = new WpeNative.EglClient
			{
				ExportEglImage = Marshal.GetFunctionPointerForDelegate(_onEglImage),
				ExportFdoEglImage = Marshal.GetFunctionPointerForDelegate(_onFdoImage),
				ExportShmBuffer = Marshal.GetFunctionPointerForDelegate(_onShm),
			};
			_exportable = WpeNative.wpe_view_backend_exportable_fdo_egl_create(_client, IntPtr.Zero, _width, _height);
			_backend = WpeNative.wpe_view_backend_exportable_fdo_get_view_backend(_exportable);
			// WebKit owns the backend: the exportable, the client struct and the callbacks are freed when it is done with it
			var webBackend = WpeNative.webkit_web_view_backend_new(_backend, Marshal.GetFunctionPointerForDelegate(s_backendDestroyed), GCHandle.ToIntPtr(_self));
			_view = WpeNative.webkit_web_view_new(webBackend);

			try
			{
				_ime = WpeImeContext.Create();
				_ime.FocusChanged += (f, p, h) => TextInputFocus?.Invoke(f, p, h);
				_ime.SurroundingChanged += t => TextInputSurrounding?.Invoke(t);
				WpeNative.webkit_web_view_set_input_method_context(_view, _ime.Handle);
			}
			catch (Exception e)
			{
				// the page still works without text input; the app sees no focus events
				WpeRuntime.Log("text input not available: " + e.Message);
			}

			WpeRuntime.ApplyBackground(_view);
			WpeRuntime.BackgroundChanged += OnBackgroundChanged;

			var settings = WpeNative.webkit_web_view_get_settings(_view);
			if (FrameBufferWebView.ApplicationName is { } name)
			{
				WpeNative.webkit_settings_set_user_agent_with_application_details(settings, name, FrameBufferWebView.ApplicationVersion);
			}

			_signals = new[]
			{
				WpeNative.g_signal_connect_data(_view, "load-changed", Marshal.GetFunctionPointerForDelegate(_onLoadChanged), IntPtr.Zero, IntPtr.Zero, 0),
				WpeNative.g_signal_connect_data(_view, "load-failed", Marshal.GetFunctionPointerForDelegate(_onLoadFailed), IntPtr.Zero, IntPtr.Zero, 0),
				WpeNative.g_signal_connect_data(_view, "notify::title", Marshal.GetFunctionPointerForDelegate(_onTitle), IntPtr.Zero, IntPtr.Zero, 0),
			};

			WpeNative.wpe_view_backend_dispatch_set_device_scale_factor(_backend, _scale);
			WpeNative.wpe_view_backend_add_activity_state(_backend, ActivityFocused | ActivityInWindow | (_visible ? ActivityVisible : 0));
			if (initialUri is not null)
			{
				WpeNative.webkit_web_view_load_uri(_view, initialUri);
			}
		}
		catch (Exception e)
		{
			_failed = true;
			Failure = e.GetType().Name + ": " + e.Message;
			WpeRuntime.Log("view creation failed: " + Failure);
		}
		List<Action> pending;
		lock (_gate)
		{
			_created = true;
			pending = new List<Action>(_beforeCreate);
			_beforeCreate.Clear();
		}
		foreach (var action in pending)
		{
			RunIfAlive(action);
		}
	}

	private void RunIfAlive(Action action)
	{
		if (_view != IntPtr.Zero && !_disposed)
		{
			action();
		}
	}

	/// <summary>Runs on the GLib thread once the view exists; dropped if creation failed or the view is gone.</summary>
	public void Post(Action action)
	{
		lock (_gate)
		{
			if (!_created)
			{
				_beforeCreate.Add(action);
				return;
			}
		}
		WpeRuntime.Run(() => RunIfAlive(action));
	}

	public void Load(string uri) => Post(() => WpeNative.webkit_web_view_load_uri(_view, uri));

	public void LoadHtml(string html) => Post(() => WpeNative.webkit_web_view_load_html(_view, html, null));

	public void LoadRequest(string uri, HttpRequestMessage request) => Post(() =>
	{
		var req = WpeNative.webkit_uri_request_new(uri);
		var headers = WpeNative.webkit_uri_request_get_http_headers(req);
		foreach (var header in request.Headers)
		{
			foreach (var value in header.Value)
			{
				WpeNative.soup_message_headers_append(headers, header.Key, value);
			}
		}
		WpeNative.webkit_web_view_load_request(_view, req);
		WpeNative.g_object_unref(req);
	});

	public void Reload() => Post(() => WpeNative.webkit_web_view_reload(_view));

	public void Stop() => Post(() => WpeNative.webkit_web_view_stop_loading(_view));

	public void GoBack() => Post(() => WpeNative.webkit_web_view_go_back(_view));

	public void GoForward() => Post(() => WpeNative.webkit_web_view_go_forward(_view));

	/// <summary>Result as JSON text like WebView2 (a string is quoted), "null" or "undefined".</summary>
	public Task<string?> ExecuteScriptAsync(string script)
	{
		var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(() =>
		{
			var handle = GCHandle.Alloc((tcs, this));
			WpeNative.webkit_web_view_evaluate_javascript(_view, script, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
				Marshal.GetFunctionPointerForDelegate(s_scriptDone), GCHandle.ToIntPtr(handle));
		});
		return tcs.Task;
	}

	private static void OnScriptDone(IntPtr source, IntPtr result, IntPtr data)
	{
		var handle = GCHandle.FromIntPtr(data);
		var (tcs, view) = ((TaskCompletionSource<string?>, WpeView))handle.Target!;
		handle.Free();
		IntPtr value = IntPtr.Zero, error = IntPtr.Zero;
		try
		{
			value = WpeNative.webkit_web_view_evaluate_javascript_finish(source, result, out error);
			if (value == IntPtr.Zero)
			{
				tcs.TrySetException(new InvalidOperationException("The script failed."));
			}
			else if (WpeNative.jsc_value_is_null(value) != 0)
			{
				tcs.TrySetResult("null");
			}
			else if (WpeNative.jsc_value_is_undefined(value) != 0)
			{
				tcs.TrySetResult("undefined");
			}
			else
			{
				var json = WpeNative.jsc_value_to_json(value, 0);
				tcs.TrySetResult(WpeNative.Utf8(json));
				WpeNative.g_free(json);
			}
		}
		catch (Exception e)
		{
			tcs.TrySetException(e);
		}
		finally
		{
			if (value != IntPtr.Zero)
			{
				WpeNative.g_object_unref(value);
			}
			if (error != IntPtr.Zero)
			{
				WpeNative.g_error_free(error);
			}
		}
	}

	/// <summary>The document title and history state, read on the GLib thread.</summary>
	public Task<(string Title, string? Uri, bool CanGoBack, bool CanGoForward)> GetStateAsync()
	{
		var tcs = new TaskCompletionSource<(string, string?, bool, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(() => tcs.TrySetResult(ReadState()));
		return tcs.Task;
	}

	private (string, string?, bool, bool) ReadState() => (
		WpeNative.Utf8(WpeNative.webkit_web_view_get_title(_view)) ?? string.Empty,
		WpeNative.Utf8(WpeNative.webkit_web_view_get_uri(_view)),
		WpeNative.webkit_web_view_can_go_back(_view) != 0,
		WpeNative.webkit_web_view_can_go_forward(_view) != 0);

	private void OnLoadChanged(IntPtr view, int loadEvent, IntPtr data)
	{
		// WebKitLoadEvent: 0 started, 1 redirected, 2 committed, 3 finished
		try
		{
			if (loadEvent == 0)
			{
				NavigationStarted?.Invoke(WpeNative.Utf8(WpeNative.webkit_web_view_get_uri(_view)) ?? string.Empty);
			}
			else if (loadEvent == 3)
			{
				var (_, uri, back, forward) = ReadState();
				NavigationFinished?.Invoke(uri ?? string.Empty, true, back, forward);
			}
		}
		catch (Exception e)
		{
			WpeRuntime.Log("load-changed handler: " + e.Message);
		}
	}

	private int OnLoadFailed(IntPtr view, int loadEvent, IntPtr failingUri, IntPtr error, IntPtr data)
	{
		try
		{
			var (_, _, back, forward) = ReadState();
			NavigationFinished?.Invoke(WpeNative.Utf8(failingUri) ?? string.Empty, false, back, forward);
		}
		catch (Exception e)
		{
			WpeRuntime.Log("load-failed handler: " + e.Message);
		}
		return 0; // WebKit shows its error page
	}

	private void OnTitle()
	{
		Title = WpeNative.Utf8(WpeNative.webkit_web_view_get_title(_view)) ?? string.Empty;
		TitleChanged?.Invoke();
	}

	// ---- size, state -------------------------------------------------------------------------------------------------

	/// <summary>Size in logical (DIP) pixels and the device scale factor; input coordinates are device pixels.</summary>
	public void SetSize(uint width, uint height, float scale)
	{
		width = Math.Max(1, width);
		height = Math.Max(1, height);
		if (!_started)
		{
			_width = width;
			_height = height;
			_scale = scale;
			WpeRuntime.Log($"view size {width}x{height} (logical), scale {scale}");
			Start();
			return;
		}
		if (width == _width && height == _height && scale == _scale)
		{
			return;
		}
		var scaleChanged = scale != _scale;
		WpeRuntime.Log($"view size {width}x{height} (logical), scale {scale}");
		_width = width;
		_height = height;
		_scale = scale;
		Post(() =>
		{
			WpeNative.wpe_view_backend_dispatch_set_size(_backend, width, height);
			if (scaleChanged)
			{
				WpeNative.wpe_view_backend_dispatch_set_device_scale_factor(_backend, scale);
			}
		});
	}

	/// <summary>Pauses or resumes rendering of a view that is not shown.</summary>
	public void SetVisible(bool visible)
	{
		_visible = visible;
		Post(() =>
		{
			if (visible)
			{
				WpeNative.wpe_view_backend_add_activity_state(_backend, ActivityVisible);
			}
			else
			{
				WpeNative.wpe_view_backend_remove_activity_state(_backend, ActivityVisible);
			}
		});
	}

	// ---- frames ------------------------------------------------------------------------------------------------------

	private void OnFrame(IntPtr image)
	{
		if (Interlocked.Increment(ref s_frames) == 1)
		{
			WpeRuntime.Log("first frame exported");
		}
		IntPtr drop;
		lock (_gate)
		{
			drop = _pending;
			_pending = image;
		}
		if (drop != IntPtr.Zero)
		{
			// replaced before it was imported: give it back and complete it so WebKit goes on
			WpeNative.wpe_view_backend_exportable_fdo_egl_dispatch_release_exported_image(_exportable, drop);
			WpeNative.wpe_view_backend_exportable_fdo_dispatch_frame_complete(_exportable);
		}
		if (!FrameBufferGpu.TryInvoke(ImportPending))
		{
			Failure = "no GPU renderer";
		}
	}

	private void ImportPending(GRContext gr)
	{
		try
		{
			IntPtr next;
			lock (_gate)
			{
				next = _pending;
				_pending = IntPtr.Zero;
			}
			if (next == IntPtr.Zero || _disposed)
			{
				return;
			}
			_bindImage ??= Marshal.GetDelegateForFunctionPointer<GlEglImageTarget>(WpeNative.eglGetProcAddress("glEGLImageTargetTexture2DOES"));
			if (_texture == 0)
			{
				WpeNative.glGenTextures(1, out _texture);
			}
			WpeNative.glBindTexture(GlTexture2D, _texture);
			WpeNative.glTexParameteri(GlTexture2D, 0x2801, 0x2601); // MIN_FILTER LINEAR
			WpeNative.glTexParameteri(GlTexture2D, 0x2800, 0x2601); // MAG_FILTER LINEAR
			WpeNative.glTexParameteri(GlTexture2D, 0x2802, 0x812F); // WRAP_S CLAMP_TO_EDGE
			WpeNative.glTexParameteri(GlTexture2D, 0x2803, 0x812F); // WRAP_T CLAMP_TO_EDGE
			_bindImage(GlTexture2D, WpeNative.wpe_fdo_egl_exported_image_get_egl_image(next));
			gr.ResetContext(); // texture binding changed behind Skia
			var width = (int)WpeNative.wpe_fdo_egl_exported_image_get_width(next);
			var height = (int)WpeNative.wpe_fdo_egl_exported_image_get_height(next);
			SKImage fresh;
			using (var backend = new GRBackendTexture(width, height, false, new GRGlTextureInfo(GlTexture2D, _texture, 0x8058))) // GL_RGBA8
			{
				fresh = SKImage.FromTexture(gr, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Premul);
			}
			if (!_firstFrameLogged)
			{
				_firstFrameLogged = true;
				WpeRuntime.Log($"first frame {width}x{height}" + (fresh is null ? ", texture import failed" : ""));
			}
			var previous = Interlocked.Exchange(ref _image, fresh);
			var old = _current;
			_current = next;
			if (previous is not null)
			{
				// recorded pictures may still reference the image: dispose it on the render thread a bit later
				Timer? timer = null;
				timer = new Timer(_ =>
				{
					timer!.Dispose();
					FrameBufferGpu.TryInvoke(_ => previous.Dispose());
				}, null, 1000, Timeout.Infinite);
			}
			FrameReady?.Invoke();
			WpeRuntime.Invoke(() =>
			{
				if (_disposed)
				{
					return;
				}
				if (old != IntPtr.Zero)
				{
					WpeNative.wpe_view_backend_exportable_fdo_egl_dispatch_release_exported_image(_exportable, old);
				}
				WpeNative.wpe_view_backend_exportable_fdo_dispatch_frame_complete(_exportable);
			});
		}
		catch (Exception e)
		{
			Failure = e.GetType().Name + ": " + e.Message;
		}
	}

	// ---- input (any thread) ------------------------------------------------------------------------------------------

	/// <summary>
	/// Pointer or touch. Coordinates in device pixels. <paramref name="phase"/>: 0 down, 1 move, 2 up.
	/// <paramref name="firstContact"/>: no other contact is down, touch points WebKit still holds are ended first.
	/// </summary>
	public void Pointer(bool touch, int id, int x, int y, int phase, bool primaryPressed, bool firstContact = false)
	{
		var time = (uint)Environment.TickCount;
		if (phase != 1)
		{
			LastUserInput = Environment.TickCount64;
		}
		if (!_inputLogged)
		{
			_inputLogged = true;
			WpeRuntime.Log($"first input {(touch ? "touch" : "pointer")} {x},{y} (view {_width}x{_height}, scale {_scale})");
		}
		if (touch)
		{
			Post(() =>
			{
				if (phase != 0 && !_touches.ContainsKey(id))
				{
					return;
				}
				if (phase == 1)
				{
					QueueMove(new WpeNative.TouchRaw { Type = 2, Time = time, Id = id, X = x, Y = y });
					return;
				}
				// moves still waiting go first: the order of the contacts stays
				FlushMoves();
				if (phase == 0 && firstContact && _touches.Count > 0)
				{
					EndStaleTouches(time);
				}
				DispatchTouch(new WpeNative.TouchRaw { Type = phase == 0 ? 1 : 3, Time = time, Id = id, X = x, Y = y }); // down, up
			});
		}
		else
		{
			var pe = new WpeNative.PointerEvent
			{
				Type = phase == 1 ? 1 : 2, // motion, button
				Time = time, X = x, Y = y,
				Button = phase == 1 ? 0u : 1u,
				State = phase == 0 ? 1u : 0u,
				Modifiers = primaryPressed ? (1u << 20) : 0u,
			};
			Post(() => WpeNative.wpe_view_backend_dispatch_pointer_event(_backend, ref pe));
		}
	}

	/// <summary>
	/// GLib thread: a move is sent at most every <see cref="TouchMoveIntervalMs"/>, with the latest position. Pages with
	/// touch listeners (Google) run script for every move and WebKit scrolls only after it: at the panel's touch rate
	/// (about 100 moves per second) the web process' main thread is saturated and scrolling stalls for seconds.
	/// </summary>
	private void QueueMove(WpeNative.TouchRaw move)
	{
		_pendingMoves[move.Id] = move;
		var wait = TouchMoveIntervalMs - (Environment.TickCount64 - _lastMoveSent);
		if (wait <= 0)
		{
			FlushMoves();
		}
		else if (!_moveFlushQueued)
		{
			_moveFlushQueued = true;
			Task.Delay((int)wait).ContinueWith(_ => Post(() =>
			{
				_moveFlushQueued = false;
				FlushMoves();
			}));
		}
	}

	/// <summary>GLib thread: sends the moves waiting in <see cref="QueueMove"/>.</summary>
	private void FlushMoves()
	{
		if (_pendingMoves.Count == 0)
		{
			return;
		}
		_lastMoveSent = Environment.TickCount64;
		foreach (var move in _pendingMoves.Values)
		{
			if (_touches.ContainsKey(move.Id))
			{
				DispatchTouch(move);
			}
		}
		_pendingMoves.Clear();
	}

	/// <summary>GLib thread: sends one touch change with all held points (the others as motion).</summary>
	private void DispatchTouch(WpeNative.TouchRaw changed)
	{
		_touches[changed.Id] = changed;
		var points = new WpeNative.TouchRaw[_touches.Count];
		var i = 0;
		foreach (var pair in _touches)
		{
			var raw = pair.Value;
			if (pair.Key != changed.Id)
			{
				raw.Type = 2;
			}
			points[i++] = raw;
		}
		fixed (WpeNative.TouchRaw* p = points)
		{
			var e = new WpeNative.TouchEvent { Points = p, Length = (ulong)points.Length, Type = changed.Type, Id = changed.Id, Time = changed.Time };
			WpeNative.wpe_view_backend_dispatch_touch_event(_backend, ref e);
		}
		if (changed.Type == 3)
		{
			_touches.Remove(changed.Id);
		}
	}

	/// <summary>GLib thread: sends the end of touch points whose release never arrived (capture lost, view swapped).</summary>
	private void EndStaleTouches(uint time)
	{
		WpeRuntime.Log($"ending {_touches.Count} stale touch point(s)");
		foreach (var stale in new List<int>(_touches.Keys))
		{
			var raw = _touches[stale];
			raw.Type = 3;
			raw.Time = time;
			_touches.Remove(stale);
			var points = new WpeNative.TouchRaw[_touches.Count + 1];
			var i = 0;
			foreach (var other in _touches.Values)
			{
				var o = other;
				o.Type = 2;
				points[i++] = o;
			}
			points[i] = raw;
			fixed (WpeNative.TouchRaw* p = points)
			{
				var e = new WpeNative.TouchEvent { Points = p, Length = (ulong)points.Length, Type = 3, Id = stale, Time = time };
				WpeNative.wpe_view_backend_dispatch_touch_event(_backend, ref e);
			}
		}
	}

	/// <summary>Wheel step (one notch up or down) at a position in device pixels.</summary>
	public void Wheel(int x, int y, int direction)
	{
		var ae = new WpeNative.AxisEvent { Type = 1, Time = (uint)Environment.TickCount, X = x, Y = y, Axis = 0, Value = direction };
		Post(() => WpeNative.wpe_view_backend_dispatch_axis_event(_backend, ref ae));
	}

	/// <summary>Key press and release of an XKB keysym (e.g. 0xff08 BackSpace, 0xff0d Return).</summary>
	public void Key(uint keysym, uint modifiers = 0) => Post(() =>
	{
		var down = new WpeNative.KeyboardEvent { Time = (uint)Environment.TickCount, KeyCode = keysym, Pressed = true, Modifiers = modifiers };
		WpeNative.wpe_view_backend_dispatch_keyboard_event(_backend, ref down);
		var up = down;
		up.Pressed = false;
		WpeNative.wpe_view_backend_dispatch_keyboard_event(_backend, ref up);
	});

	/// <summary>
	/// Replaces the whole value of the focused editable element: select all (Ctrl+A), then commit the new text (or delete when
	/// empty), then press Return like a physical keyboard (forms submit). The element is blurred afterwards, so the next
	/// tap raises a focus event again.
	/// </summary>
	public void ReplaceText(string text) => Post(() =>
	{
		var time = (uint)Environment.TickCount;
		var selectAll = new WpeNative.KeyboardEvent { Time = time, KeyCode = 0x61, Pressed = true, Modifiers = 1 }; // Ctrl+A
		WpeNative.wpe_view_backend_dispatch_keyboard_event(_backend, ref selectAll);
		selectAll.Pressed = false;
		WpeNative.wpe_view_backend_dispatch_keyboard_event(_backend, ref selectAll);
		if (text.Length == 0)
		{
			var back = new WpeNative.KeyboardEvent { Time = time, KeyCode = 0xff08, Pressed = true };
			WpeNative.wpe_view_backend_dispatch_keyboard_event(_backend, ref back);
			back.Pressed = false;
			WpeNative.wpe_view_backend_dispatch_keyboard_event(_backend, ref back);
		}
		else
		{
			_ime?.Commit(text);
		}
		// the commit travels to the web process asynchronously (IME channel): Return and blur follow once it is applied
		Task.Delay(150).ContinueWith(_ =>
		{
			Key(0xff0d);
			Task.Delay(400).ContinueWith(_ => Blur());
		});
	});

	/// <summary>Drops the edit focus so the next tap on a field raises a focus event (WebKit reports focus only when editing starts or ends).</summary>
	public void Blur() => Post(() => WpeNative.webkit_web_view_evaluate_javascript(_view,
		"document.activeElement && document.activeElement.blur()", -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

	// ---- end of life -------------------------------------------------------------------------------------------------

	// GLib thread
	private void OnBackgroundChanged() => WpeRuntime.ApplyBackground(_view);

	/// <summary>Releases the view and its frames. Any thread, once.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (!_started || _failed || WpeRuntime.Failure is not null)
		{
			_self.Free();
			return;
		}
		WpeRuntime.Run(() =>
		{
			if (_view == IntPtr.Zero)
			{
				// creation failed before the backend went to WebKit
				FreeNative();
				return;
			}
			WpeRuntime.BackgroundChanged -= OnBackgroundChanged;
			foreach (var id in _signals)
			{
				WpeNative.g_signal_handler_disconnect(_view, id);
			}
			_ime?.Release();
			ReleaseFrames();
			var view = _view;
			_view = IntPtr.Zero;
			// WebKit may keep the process of a closed view cached: end it, the view is gone for good
			WpeNative.webkit_web_view_terminate_web_process(view);
			// the rest happens in OnBackendDestroyed, once WebKit is done with the backend (it may still call it now)
			WpeNative.g_object_unref(view);
		});
	}

	private void ReleaseFrames()
	{
		IntPtr pending, current;
		lock (_gate)
		{
			pending = _pending;
			_pending = IntPtr.Zero;
			current = _current;
			_current = IntPtr.Zero;
		}
		if (_exportable == IntPtr.Zero)
		{
			return;
		}
		if (pending != IntPtr.Zero)
		{
			WpeNative.wpe_view_backend_exportable_fdo_egl_dispatch_release_exported_image(_exportable, pending);
		}
		if (current != IntPtr.Zero)
		{
			WpeNative.wpe_view_backend_exportable_fdo_egl_dispatch_release_exported_image(_exportable, current);
		}
	}

	// GLib thread, called by WebKit when the view backend is destroyed (after the last reference to the view)
	private static void OnBackendDestroyed(IntPtr data)
	{
		var view = (WpeView)GCHandle.FromIntPtr(data).Target!;
		WpeRuntime.Log("view backend destroyed");
		view.ReleaseFrames();
		view.FreeNative();
	}

	private void FreeNative()
	{
		if (_exportable != IntPtr.Zero)
		{
			WpeNative.wpe_view_backend_exportable_fdo_destroy(_exportable);
			_exportable = IntPtr.Zero;
		}
		if (_client != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(_client);
			_client = IntPtr.Zero;
		}
		FrameBufferGpu.TryInvoke(_ =>
		{
			Interlocked.Exchange(ref _image, null)?.Dispose();
			if (_texture != 0)
			{
				WpeNative.glDeleteTextures(1, ref _texture);
				_texture = 0;
			}
		});
		if (_self.IsAllocated)
		{
			_self.Free();
		}
	}
}
