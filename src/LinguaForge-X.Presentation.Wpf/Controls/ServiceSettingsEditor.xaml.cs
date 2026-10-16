using System.Windows;
using System.Windows.Controls;
using LinguaForge_X.Domain.Models;

namespace LinguaForge_X.Presentation.Wpf.Controls;

public partial class ServiceSettingsEditor : UserControl
{
    public static IReadOnlyList<KeyValuePair<ServiceProviderKind, string>> Providers { get; } =
    [new(ServiceProviderKind.AzureOpenAI, "Azure OpenAI"), new(ServiceProviderKind.AzureSpeech, "Azure AI Speech")];
    private static IReadOnlyList<KeyValuePair<ServiceProviderKind, string>> SynthesisProviders { get; } =
        [.. Providers, new(ServiceProviderKind.LocalGptSoVits, "本地 GPT-SoVITS")];
    public static readonly DependencyProperty AllowLocalVoiceProperty = DependencyProperty.Register(
        nameof(AllowLocalVoice), typeof(bool), typeof(ServiceSettingsEditor), new PropertyMetadata(false, (obj, args) =>
        {
            var editor = (ServiceSettingsEditor)obj;
            if (editor.ProviderSelector is not null)
                editor.ProviderSelector.ItemsSource = (bool)args.NewValue ? SynthesisProviders : Providers;
        }));
    public bool AllowLocalVoice { get => (bool)GetValue(AllowLocalVoiceProperty); set => SetValue(AllowLocalVoiceProperty, value); }
    public ServiceSettingsEditor() => InitializeComponent();
}

public static class PasswordBinding
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PasswordBinding), new PropertyMetadata(false, (obj, args) =>
        {
            if (obj is not PasswordBox box) return;
            box.PasswordChanged -= OnBoxPasswordChanged;
            if ((bool)args.NewValue) box.PasswordChanged += OnBoxPasswordChanged;
        }));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(PasswordBinding), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));
    public static string GetPassword(DependencyObject obj) => (string)obj.GetValue(PasswordProperty);
    public static void SetPassword(DependencyObject obj, string value) => obj.SetValue(PasswordProperty, value);
    private static void OnPasswordChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not PasswordBox box) return;
        box.PasswordChanged -= OnBoxPasswordChanged;
        if (box.Password != (string?)e.NewValue) box.Password = (string?)e.NewValue ?? "";
        if (GetEnabled(box)) box.PasswordChanged += OnBoxPasswordChanged;
    }
    private static void OnBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetCurrentValue(PasswordProperty, box.Password);
    }
}
