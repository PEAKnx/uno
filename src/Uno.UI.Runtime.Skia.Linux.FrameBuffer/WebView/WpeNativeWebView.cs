// Added by PEAKnx GmbH (2026), see https://github.com/PEAKnx/uno/commits/pnx/6.7.135
#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Uno.Foundation.Logging;
using Uno.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace Uno.UI.Runtime.Skia.Wpe;

internal sealed class WpeNativeWebViewProvider(CoreWebView2 coreWebView2) : INativeWebViewProvider
{
	INativeWebView INativeWebViewProvider.CreateNativeWebView(ContentPresenter contentPresenter)
	{
		if (WpeRuntime.Failure is { } failure)
		{
			throw new PlatformNotSupportedException("WPE WebKit cannot be used: " + failure);
		}
		return new WpeNativeWebView(coreWebView2, contentPresenter);
	}
}

/// <summary>The <see cref="INativeWebView"/> of the FrameBuffer host: a <see cref="WpeView"/> shown by a <see cref="WpeCanvasElement"/>.</summary>
internal sealed class WpeNativeWebView : ICleanableNativeWebView
{
	private readonly CoreWebView2 _coreWebView;
	private readonly ContentPresenter _presenter;
	private readonly DispatcherQueue _dispatcher;
	// an unloaded view gives its web process back after this time; shown again, it is created anew (history is lost)
	private static readonly TimeSpan ReleaseDelay = TimeSpan.FromSeconds(10);

	private readonly WpeCanvasElement _canvas;
	private WpeView _view;
	private bool _released;
	private string? _lastUri; // never logged: may carry credentials
	private DispatcherQueueTimer? _releaseTimer;
	private bool _dontRaiseNextNavigationCompleted;
	private bool _scrollingEnabled = true;
	private string? _lastFocusValue;
	private int _focusSequence;
	private FrameBufferWebViewTextInputRequest? _openRequest;

	public WpeNativeWebView(CoreWebView2 coreWebView2, ContentPresenter presenter)
	{
		_coreWebView = coreWebView2;
		_presenter = presenter;
		_dispatcher = presenter.DispatcherQueue;
		_view = CreateView(null);
		_canvas = new WpeCanvasElement(_view);
		presenter.Content = _canvas;
	}

	~WpeNativeWebView() => _view.Dispose();

	private WpeView CreateView(string? uri)
	{
		// the scale is known here: WebKit does not pick up a later change of the device scale factor reliably
		var view = new WpeView(800, 600, (float)(_presenter.XamlRoot?.RasterizationScale ?? 1), uri);
		// weak: the view lives until it is disposed, the finalizer of this object disposes it
		var weak = new WeakReference<WpeNativeWebView>(this);
		view.NavigationStarted += u => { if (weak.TryGetTarget(out var t)) t.OnNavigationStarted(u); };
		view.NavigationFinished += (u, ok, back, fwd) => { if (weak.TryGetTarget(out var t)) t.OnNavigationFinished(u, ok, back, fwd); };
		view.TitleChanged += () => { if (weak.TryGetTarget(out var t)) t._dispatcher.TryEnqueue(t._coreWebView.OnDocumentTitleChanged); };
		view.TextInputFocus += (f, p, h) => { if (weak.TryGetTarget(out var t)) t.OnTextInputFocus(f, p, h); };
		view.TextInputSurrounding += value => { if (weak.TryGetTarget(out var t)) t._lastFocusValue = value; };
		return view;
	}

	/// <summary>The live view; a released one is created again with the last address (UI thread).</summary>
	private WpeView View
	{
		get
		{
			if (_released)
			{
				_released = false;
				_view = CreateView(_lastUri);
				_canvas.SetView(_view);
			}
			return _view;
		}
	}

	public string DocumentTitle => _view.Title;

	public void OnLoaded()
	{
		_releaseTimer?.Stop();
		View.SetVisible(true);
	}

	public void OnUnloaded()
	{
		_view.SetVisible(false);
		if (_released)
		{
			return;
		}
		if (_releaseTimer is null)
		{
			_releaseTimer = _dispatcher.CreateTimer();
			_releaseTimer.IsRepeating = false;
			_releaseTimer.Interval = ReleaseDelay;
			_releaseTimer.Tick += (_, _) => Release();
		}
		_releaseTimer.Start();
	}

	// RAM: each view has its own web process (about 110 MB); an unloaded one (page left, fullscreen closed) frees it
	private void Release()
	{
		if (_released || _presenter.IsLoaded)
		{
			return;
		}
		_released = true;
		_view.Dispose();
		WpeRuntime.Log("unloaded view released");
	}

	public void GoBack() => View.GoBack();

	public void GoForward() => View.GoForward();

	public void Stop() => View.Stop();

	public void Reload() => View.Reload();

	public void ProcessNavigation(Uri uri)
	{
		if (_coreWebView.HostToFolderMap.TryGetValue(uri.Host.ToLowerInvariant(), out var folderName))
		{
			View.Load($"file://{Path.Join(Package.Current.InstalledPath, folderName, uri.PathAndQuery)}");
		}
		else
		{
			View.Load(uri.ToString());
		}
	}

	public void ProcessNavigation(string html) => View.LoadHtml(html);

	public void ProcessNavigation(HttpRequestMessage httpRequestMessage)
	{
		var uri = httpRequestMessage.RequestUri?.ToString();
		if (uri is null)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"{nameof(ProcessNavigation)} received an {nameof(HttpRequestMessage)} without a uri.");
			}
			return;
		}
		if (httpRequestMessage.Method != HttpMethod.Get && this.Log().IsEnabled(LogLevel.Warning))
		{
			this.Log().Warn($"Only GET is supported, {httpRequestMessage.Method} was sent as GET.");
		}
		View.LoadRequest(uri, httpRequestMessage);
	}

	public Task<string?> ExecuteScriptAsync(string script, CancellationToken token) => View.ExecuteScriptAsync(script);

	public Task<string?> InvokeScriptAsync(string script, string[]? arguments, CancellationToken token)
	{
		// the arguments are JSON-escaped and joined with commas; [1..^1] removes the brackets
		var argumentString = arguments is not null ? JsonSerializer.Serialize(arguments)[1..^1] : "";
		return ExecuteScriptAsync($"{script}({argumentString})", token);
	}

	public void SetScrollingEnabled(bool isScrollingEnabled)
	{
		_scrollingEnabled = isScrollingEnabled;
		ApplyScrolling();
	}

	private void ApplyScrolling()
	{
		var overflow = _scrollingEnabled ? "" : "hidden";
		_ = _view.ExecuteScriptAsync($"document.documentElement.style.overflow='{overflow}';document.body&&(document.body.style.overflow='{overflow}')");
	}

	// ---- navigation events (GLib thread -> UI thread) ----------------------------------------------------------------

	private void OnNavigationStarted(string uriString)
	{
		if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
		{
			return;
		}
		_dispatcher.TryEnqueue(() =>
		{
			_coreWebView.RaiseNavigationStarting(uri, out var cancel);
			if (cancel)
			{
				_view.Stop();
			}
		});
	}

	private void OnNavigationFinished(string uriString, bool success, bool canGoBack, bool canGoForward)
	{
		if (success && uriString.Length > 0)
		{
			_lastUri = uriString;
		}
		if (!success)
		{
			_dontRaiseNextNavigationCompleted = true;
		}
		else if (_dontRaiseNextNavigationCompleted)
		{
			// a failed load is followed by the load of WebKit's error page
			_dontRaiseNextNavigationCompleted = false;
			return;
		}
		Uri.TryCreate(uriString, UriKind.Absolute, out var uri);
		_dispatcher.TryEnqueue(() =>
		{
			if (success)
			{
				_coreWebView.SetHistoryProperties(canGoBack, canGoForward);
				_coreWebView.RaiseHistoryChanged();
				if (!_scrollingEnabled)
				{
					ApplyScrolling();
				}
			}
			_coreWebView.RaiseNavigationCompleted(uri, isSuccess: success, httpStatusCode: success ? 200 : 0,
				errorStatus: CoreWebView2WebErrorStatus.Unknown, shouldSetSource: true);
		});
	}

	// ---- text input --------------------------------------------------------------------------------------------------

	private void OnTextInputFocus(bool focused, int purpose, int hints)
	{
		var sequence = Interlocked.Increment(ref _focusSequence);
		if (!focused)
		{
			_dispatcher.TryEnqueue(() =>
			{
				_openRequest?.End();
				_openRequest = null;
			});
			return;
		}
		if (!FrameBufferWebView.HasTextInputHandler)
		{
			return;
		}
		// WebKit reports the text of the element right after the focus: wait briefly for it as the keyboard's start value
		_lastFocusValue = string.Empty;
		Task.Delay(120).ContinueWith(_ =>
		{
			if (sequence != Volatile.Read(ref _focusSequence))
			{
				return;
			}
			var value = _lastFocusValue ?? string.Empty;
			_dispatcher.TryEnqueue(() =>
			{
				var kind = Enum.IsDefined(typeof(FrameBufferWebViewInputPurpose), purpose) ? (FrameBufferWebViewInputPurpose)purpose : FrameBufferWebViewInputPurpose.FreeForm;
				var request = new FrameBufferWebViewTextInputRequest(kind, value, _view.ReplaceText, _view.Blur);
				_openRequest = request;
				FrameBufferWebView.RaiseTextInput(request);
			});
		});
	}
}
