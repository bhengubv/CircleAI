namespace CircleAI.Samples.It.App;

/// <summary>Hosts the shared UI in a web view.</summary>
public partial class MainPage : ContentPage
{
    /// <summary>Create the page.</summary>
    public MainPage()
    {
        InitializeComponent();
        // HandlerChanged, because it fires once the platform view actually exists.
        // Reading Handler.PlatformView from the page constructor or from
        // BlazorWebViewInitialized is too early: it is null, the cast fails
        // silently, and the setting below never happens.
        blazorWebView.HandlerChanged += OnWebViewHandlerChanged;
    }

    /// <summary>
    /// Pin the web view's text zoom to 100%.
    /// </summary>
    /// <remarks>
    /// ANDROID'S WEBVIEW APPLIES A FONT SCALE ON TOP OF THE CSS, and the native
    /// screens do not - they set sizes in sp, which the platform has already
    /// scaled once. Left alone the shared UI is scaled a second time and the two
    /// apps stop being the same screen. On this P30 the system reports
    /// font_scale 1.0 while HwTypeface reports 0.95, so the two paths do not even
    /// agree with each other.
    /// <para>
    /// NOT an accessibility regression: the shared CSS is in relative units and
    /// the browser's own zoom still applies. What is removed is the double
    /// application.
    /// </para>
    /// </remarks>
    private void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
#if ANDROID
        if (blazorWebView.Handler?.PlatformView is Android.Webkit.WebView native
            && native.Settings is { } settings)
        {
            settings.TextZoom = 100;
        }
#endif
    }
}
