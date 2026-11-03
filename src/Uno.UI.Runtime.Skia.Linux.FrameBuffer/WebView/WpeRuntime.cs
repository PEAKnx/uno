// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Uno.Foundation.Logging;

namespace Uno.UI.Runtime.Skia.Wpe;

/// <summary>
/// Process-wide WPE state: engine detection, the GLib thread (WebKit's main run loop lives on the default GLib main
/// context, which this thread owns and runs) and the one-time wpebackend-fdo initialization with the host's EGL display.
/// </summary>
internal static class WpeRuntime
{
	private static readonly object s_gate = new();
	private static readonly Queue<Action> s_early = new();
	private static readonly WpeNative.GSourceFunc s_trampoline = RunQueued;
	private static bool? s_installed;
	private static bool s_starting;
	private static volatile bool s_ready;
	private static IntPtr s_context;
	private static string? s_failure;
	// dark mode of the pages: -1 not set, 0 light, 1 dark
	private static int s_darkMode = -1;
	// background where a page has not painted yet (ARGB), -1: WebKit's default (white)
	private static long s_background = -1;
	private static int s_liveViews;

	// Every live view has its own web process: 150-170 MB RSS (ADR 0001 section 10). The views may use this share of
	// the RAM; 4 GB devices get 6 views, 2 GB devices 3.
	private const int ViewRamPercent = 30;
	private const int ViewRamMb = 170;
	private const int MaxLiveViewsCap = 8;

	/// <summary>
	/// Number of web views that may be live at once (a web process each), from the RAM of the device. The environment
	/// variable <c>UNO_FRAMEBUFFER_WEBVIEW_MAX</c> overrides it (0 refuses every view: to see the message of the app).
	/// </summary>
	public static int MaxLiveViews { get; } = ComputeMaxLiveViews();

	private static int ComputeMaxLiveViews()
	{
		if (int.TryParse(Environment.GetEnvironmentVariable("UNO_FRAMEBUFFER_WEBVIEW_MAX"), out var forced) && forced >= 0)
		{
			return forced;
		}
		long totalMb = 0;
		try
		{
			// "MemTotal:  3912345 kB"
			var line = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
			if (line is not null && long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var kb))
			{
				totalMb = kb / 1024;
			}
		}
		catch (Exception e)
		{
			Log("RAM of the device unknown: " + e.Message);
		}
		var limit = totalMb > 0 ? (int)Math.Clamp(totalMb * ViewRamPercent / 100 / ViewRamMb, 1, MaxLiveViewsCap) : 2;
		Log($"web views: at most {limit} live at once (RAM {(totalMb > 0 ? totalMb + " MB" : "unknown")})");
		return limit;
	}

	/// <summary>GLib thread: a view is about to be created. False (and why) when the limit is reached.</summary>
	internal static bool TryAcquireView(out string reason)
	{
		var live = Interlocked.Increment(ref s_liveViews);
		if (live > MaxLiveViews)
		{
			Interlocked.Decrement(ref s_liveViews);
			reason = $"too many web views: {MaxLiveViews} live at the most for the RAM of this device";
			return false;
		}
		reason = string.Empty;
		return true;
	}

	/// <summary>A view acquired with <see cref="TryAcquireView"/> is gone.</summary>
	internal static void ReleaseView() => Interlocked.Decrement(ref s_liveViews);

	/// <summary>
	/// Schemes a page may navigate to (links, redirects, forms). Local files stay out of reach of web content; the
	/// app's own mapped folders are checked by the view.
	/// </summary>
	internal static bool IsAllowedScheme(string scheme) => scheme is "http" or "https" or "about" or "data" or "blob";

	private static readonly List<string> s_allowedFolders = new();

	/// <summary>A folder of the app (CoreWebView2 virtual host mapping) whose files pages may open.</summary>
	internal static void AllowFolder(string folder)
	{
		var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		lock (s_allowedFolders)
		{
			if (!s_allowedFolders.Contains(full))
			{
				s_allowedFolders.Add(full);
			}
		}
	}

	internal static bool IsAllowedFile(string path)
	{
		string full;
		try
		{
			full = Path.GetFullPath(path);
		}
		catch (ArgumentException)
		{
			return false;
		}
		lock (s_allowedFolders)
		{
			return s_allowedFolders.Any(folder => full.StartsWith(folder, StringComparison.Ordinal));
		}
	}

	/// <summary>GLib thread: the background color changed, the views apply it (<see cref="ApplyBackground"/>).</summary>
	internal static event Action? BackgroundChanged;

	[DllImport("libc", SetLastError = true)]
	private static extern int setenv(string name, string value, int overwrite);

	/// <summary>True if libWPEWebKit, libWPEBackend-fdo and libwpe load. Checked once; never creates anything.</summary>
	public static bool IsInstalled
	{
		get
		{
			lock (s_gate)
			{
				if (s_installed is null)
				{
					var missing = new List<string>();
					foreach (var lib in new[] { WpeNative.Web, WpeNative.Fdo, WpeNative.Wpe })
					{
						if (NativeLibrary.TryLoad(lib, out var handle))
						{
							NativeLibrary.Free(handle);
						}
						else
						{
							missing.Add(lib);
						}
					}
					s_installed = missing.Count == 0;
					Log(s_installed.Value ? "WPE WebKit found" : "WPE WebKit not available, missing " + string.Join(", ", missing));
				}
				return s_installed.Value;
			}
		}
	}

	/// <summary>Why the runtime failed to start (null while it is running or not started).</summary>
	public static string? Failure => s_failure;

	/// <summary>
	/// Color scheme of all pages (<c>prefers-color-scheme</c>). Any thread; applied when the runtime starts, so the call
	/// does not start WebKit by itself.
	/// </summary>
	public static void SetDarkMode(bool dark)
	{
		Interlocked.Exchange(ref s_darkMode, dark ? 1 : 0);
		lock (s_gate)
		{
			if (!s_starting && !s_ready)
			{
				return;
			}
		}
		Run(ApplyDarkMode);
	}

	/// <summary>
	/// Background of all views where the page has not painted yet: tiles not rendered while scrolling, pages without a
	/// background. Any thread.
	/// </summary>
	public static void SetBackgroundColor(byte a, byte r, byte g, byte b)
	{
		Interlocked.Exchange(ref s_background, ((long)a << 24) | ((long)r << 16) | ((long)g << 8) | b);
		lock (s_gate)
		{
			if (!s_ready)
			{
				// views apply it when they are created
				return;
			}
		}
		Run(() => BackgroundChanged?.Invoke());
	}

	/// <summary>GLib thread: sets the background color on <paramref name="view"/> (WebKitWebView*), if one was set.</summary>
	internal static void ApplyBackground(IntPtr view)
	{
		var argb = Interlocked.Read(ref s_background);
		if (argb < 0 || view == IntPtr.Zero)
		{
			return;
		}
		var color = new WpeNative.WebKitColor
		{
			Red = ((argb >> 16) & 0xff) / 255.0,
			Green = ((argb >> 8) & 0xff) / 255.0,
			Blue = (argb & 0xff) / 255.0,
			Alpha = ((argb >> 24) & 0xff) / 255.0,
		};
		WpeNative.webkit_web_view_set_background_color(view, ref color);
	}

	// GLib thread
	private static void ApplyDarkMode()
	{
		var dark = Volatile.Read(ref s_darkMode);
		if (dark < 0)
		{
			return;
		}
		try
		{
			WpeNative.webkit_pnx_set_dark_mode(dark);
		}
		catch (EntryPointNotFoundException)
		{
			Log("dark mode not supported by this WPE WebKit (BSP patch missing): pages keep their default scheme");
		}
	}

	/// <summary>
	/// Runs <paramref name="action"/> on the GLib thread, as soon as the runtime is up. Starts the runtime on the first call:
	/// the EGL display is taken on the host's render thread.
	/// </summary>
	public static void Run(Action action)
	{
		bool start = false;
		lock (s_gate)
		{
			if (s_failure is not null)
			{
				return;
			}
			if (!s_ready)
			{
				s_early.Enqueue(action);
				start = !s_starting;
				s_starting = true;
			}
		}
		if (s_ready)
		{
			Invoke(action);
		}
		else if (start && !FrameBufferGpu.TryInvoke(_ => Start(WpeNative.eglGetCurrentDisplay())))
		{
			Fail("the host has no GPU renderer");
		}
	}

	private static void Start(IntPtr eglDisplay)
	{
		CheckEglWayland(eglDisplay);
		var thread = new Thread(() =>
		{
			try
			{
				SetProgramName();
				PrepareEnvironment();
				if (WpeNative.wpe_loader_init(WpeNative.Fdo) == 0)
				{
					Fail("wpe_loader_init failed");
					return;
				}
				if (WpeNative.wpe_fdo_initialize_for_egl_display(eglDisplay) == 0)
				{
					Fail("wpe_fdo_initialize_for_egl_display failed");
					return;
				}
				s_context = WpeNative.g_main_context_default();
				try
				{
					PersistCookies();
				}
				catch (Exception e)
				{
					// web views still work, with cookies in memory
					Log("persistent cookies not available: " + e.Message);
				}
				// before the first view: its page starts with the scheme
				ApplyDarkMode();
				List<Action> early;
				lock (s_gate)
				{
					early = new List<Action>(s_early);
					s_early.Clear();
					s_ready = true;
				}
				// this thread is the GLib thread: run what was waiting for the runtime, then serve the loop
				foreach (var a in early)
				{
					RunGuarded(a);
				}
				var loop = WpeNative.g_main_loop_new(s_context, 0);
				WpeNative.g_main_loop_run(loop);
			}
			catch (Exception e)
			{
				Fail(e.GetType().Name + ": " + e.Message);
			}
		})
		{ IsBackground = true, Name = "WPE GLib" };
		thread.Start();
	}

	/// <summary>
	/// wpebackend-fdo takes the frames of the web process as Wayland buffers: without EGL_WL_bind_wayland_display
	/// (Mesa built without the Wayland platform) the web process never delivers a frame and web views stay blank.
	/// </summary>
	private static void CheckEglWayland(IntPtr eglDisplay)
	{
		const int EGL_EXTENSIONS = 0x3055;
		var extensions = WpeNative.Utf8(WpeNative.eglQueryString(eglDisplay, EGL_EXTENSIONS)) ?? "";
		if (!extensions.Split(' ').Contains("EGL_WL_bind_wayland_display") && typeof(WpeRuntime).Log().IsEnabled(LogLevel.Warning))
		{
			typeof(WpeRuntime).Log().Warn("[wpe] EGL lacks EGL_WL_bind_wayland_display (Mesa without the Wayland platform?): web views stay blank. "
				+ "This extension is deprecated in Mesa: consider moving the WebView backend from libwpe/wpebackend-fdo to WPEPlatform (dma-buf, no Wayland).");
		}
	}

	/// <summary>
	/// The default session keeps its data in <c>$XDG_DATA_HOME/&lt;GLib program name&gt;</c>. Without a name the WPE
	/// libraries set "&lt;unknown&gt;": the app's assembly name is set before the session exists.
	/// </summary>
	private static void SetProgramName()
	{
		var current = WpeNative.Utf8(WpeNative.g_get_prgname());
		if ((current is null || current == "<unknown>") && Assembly.GetEntryAssembly()?.GetName().Name is { } name)
		{
			WpeNative.g_set_prgname(name);
		}
	}

	/// <summary>
	/// GLib thread, before the first view: cookies of the default session are kept in its data directory
	/// (<c>cookies.sqlite</c>), so consents and logins of web pages survive a restart.
	/// </summary>
	private static void PersistCookies()
	{
		const int WEBKIT_COOKIE_PERSISTENT_STORAGE_SQLITE = 1;
		var session = WpeNative.webkit_network_session_get_default();
		var dir = WpeNative.Utf8(WpeNative.webkit_website_data_manager_get_base_data_directory(WpeNative.webkit_network_session_get_website_data_manager(session)));
		if (string.IsNullOrEmpty(dir))
		{
			Log("no website data directory: cookies are kept in memory only");
			return;
		}
		Directory.CreateDirectory(dir);
		WpeNative.webkit_cookie_manager_set_persistent_storage(WpeNative.webkit_network_session_get_cookie_manager(session), Path.Combine(dir, "cookies.sqlite"), WEBKIT_COOKIE_PERSISTENT_STORAGE_SQLITE);
		Log("cookies persisted in the website data directory");
	}

	/// <summary>
	/// The host is DRM master on the primary node: the web process has to render on a render node. Sets
	/// <c>WPE_DRM_RENDER_NODE</c> and <c>WPE_DRM_DEVICE</c> (inherited by the web process) unless they are set.
	/// </summary>
	private static void PrepareEnvironment()
	{
		var node = Environment.GetEnvironmentVariable("WPE_DRM_RENDER_NODE");
		if (string.IsNullOrEmpty(node))
		{
			node = Directory.Exists("/dev/dri") ? Directory.GetFiles("/dev/dri", "renderD*").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null;
			if (node is null)
			{
				Log("no DRM render node: the web process may fail to render");
				return;
			}
			setenv("WPE_DRM_RENDER_NODE", node, 0);
		}
		setenv("WPE_DRM_DEVICE", node, 0);
		CheckSandbox();
	}

	/// <summary>
	/// WebKit's sandbox (bubblewrap) needs user namespaces unless the process is root. Without them the web process
	/// cannot start and WebKit aborts the whole app (GLib error). Checked once: if bubblewrap fails here, the sandbox
	/// is switched off for this process and a warning is logged (security gap: the web process then runs unconfined).
	/// </summary>
	private static void CheckSandbox()
	{
		const string bwrap = "/usr/bin/bwrap";
		if (!File.Exists(bwrap) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS")))
		{
			return;
		}
		var ok = false;
		try
		{
			var psi = new ProcessStartInfo(bwrap) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
			foreach (var arg in new[] { "--unshare-user-try", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--ro-bind", "/", "/", "true" })
			{
				psi.ArgumentList.Add(arg);
			}
			using var probe = Process.Start(psi);
			if (probe is not null && probe.WaitForExit(5000))
			{
				ok = probe.ExitCode == 0;
			}
		}
		catch (Exception e)
		{
			Log("sandbox check failed: " + e.Message);
		}
		if (!ok)
		{
			setenv("WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS", "1", 1);
			if (typeof(WpeRuntime).Log().IsEnabled(LogLevel.Warning))
			{
				typeof(WpeRuntime).Log().Warn("[wpe] bubblewrap cannot create namespaces for this user (kernel without user namespaces?): web content runs without sandbox");
			}
		}
	}

	/// <summary>Runs <paramref name="action"/> on the GLib thread. The runtime must be ready (any thread).</summary>
	public static void Invoke(Action action)
	{
		var handle = GCHandle.Alloc(action);
		WpeNative.g_main_context_invoke(s_context, Marshal.GetFunctionPointerForDelegate(s_trampoline), GCHandle.ToIntPtr(handle));
	}

	private static int RunQueued(IntPtr data)
	{
		var handle = GCHandle.FromIntPtr(data);
		var action = (Action)handle.Target!;
		handle.Free();
		RunGuarded(action);
		return 0;
	}

	private static void RunGuarded(Action action)
	{
		try
		{
			action();
		}
		catch (Exception e)
		{
			// an exception must not end the GLib thread: every WPE view would stop working
			Log("exception on the GLib thread: " + e);
		}
	}

	private static void Fail(string reason)
	{
		lock (s_gate)
		{
			s_failure = reason;
			s_early.Clear();
		}
		Log("WPE runtime failed: " + reason);
	}

	internal static void Log(string message)
	{
		if (typeof(WpeRuntime).Log().IsEnabled(LogLevel.Information))
		{
			typeof(WpeRuntime).Log().Info("[wpe] " + message);
		}
	}
}
