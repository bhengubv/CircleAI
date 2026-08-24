namespace CircleAI.Samples.It.App;

/// <summary>The application.</summary>
public partial class App : Application
{
    /// <summary>Create the application.</summary>
    public App() => InitializeComponent();

    /// <inheritdoc />
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new MainPage()) { Title = "Circle AI" };
}
