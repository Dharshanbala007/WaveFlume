using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WaveFlume.App;

/// <summary>"Enter applies" (spec §11.0): pressing Enter in a TextBox pushes its value to the binding.</summary>
public static class InputBehaviors
{
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter", typeof(bool), typeof(InputBehaviors), new PropertyMetadata(false, OnChanged));

    public static bool GetCommitOnEnter(DependencyObject d) => (bool)d.GetValue(CommitOnEnterProperty);
    public static void SetCommitOnEnter(DependencyObject d, bool value) => d.SetValue(CommitOnEnterProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.KeyDown -= OnKeyDown;
        if ((bool)e.NewValue) box.KeyDown += OnKeyDown;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box && !box.AcceptsReturn)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
