using System;
using System.Windows;
using System.Windows.Controls;

namespace BARS_Client_V2.Presentation.Behaviors;

// Enables two-way binding of PasswordBox.Password (which is not a DP by default)
public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword",
        typeof(string),
        typeof(PasswordBoxAssistant),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty = DependencyProperty.RegisterAttached(
        "BindPassword",
        typeof(bool),
        typeof(PasswordBoxAssistant),
        new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty UpdatingPasswordProperty = DependencyProperty.RegisterAttached(
        "UpdatingPassword",
        typeof(bool),
        typeof(PasswordBoxAssistant),
        new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject dp) => (string)dp.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject dp, string value) => dp.SetValue(BoundPasswordProperty, value);

    public static bool GetBindPassword(DependencyObject dp) => (bool)dp.GetValue(BindPasswordProperty);
    public static void SetBindPassword(DependencyObject dp, bool value) => dp.SetValue(BindPasswordProperty, value);

    private static bool GetUpdatingPassword(DependencyObject dp) => (bool)dp.GetValue(UpdatingPasswordProperty);
    private static void SetUpdatingPassword(DependencyObject dp, bool value) => dp.SetValue(UpdatingPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (!GetBindPassword(box)) return;

        // Avoid recursive update
        if (GetUpdatingPassword(box)) return;

        var newPassword = e.NewValue as string ?? string.Empty;
        if (box.Password != newPassword)
        {
            box.Password = newPassword;
        }
    }

    private static void OnBindPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is not PasswordBox box) return;

        if ((bool)e.OldValue)
        {
            box.PasswordChanged -= HandlePasswordChanged;
        }
        if ((bool)e.NewValue)
        {
            box.PasswordChanged += HandlePasswordChanged;
        }
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        SetUpdatingPassword(box, true);
        SetBoundPassword(box, box.Password);
        SetUpdatingPassword(box, false);
    }
}
