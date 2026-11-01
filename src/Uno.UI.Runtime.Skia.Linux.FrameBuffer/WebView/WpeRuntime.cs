// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
