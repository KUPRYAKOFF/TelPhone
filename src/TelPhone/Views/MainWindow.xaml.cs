using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TelPhone.Core;
using TelPhone.ViewModels;

namespace TelPhone.Views;

public partial class MainWindow : Window
{
    private const double FullMinWidth = 390;
    private const double FullMinHeight = 505;
    private static readonly Size CompactSize = new(350, 238);
    private readonly MainViewModel viewModel;
    private IntPtr windowHandle;
    private bool compact;
    private bool stacked;
    private bool resizing;
    private Rect fullBounds;
    private WindowState fullState;
    private TabItem? normalTab;

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnAppearanceChanged;
        SourceInitialized += (_, _) => windowHandle = new WindowInteropHelper(this).Handle;
        SizeChanged += (_, _) => UpdateLayoutMode();
        LocationChanged += (_, _) => UpdateLayoutMode();
        StateChanged += (_, _) => UpdateLayoutMode();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        UpdateLayoutMode();
        NumberInput.Focus();
        await viewModel.StartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.PropertyChanged -= OnAppearanceChanged;
        base.OnClosed(e);
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Theme))
            ApplyTheme();
        else if (e.PropertyName == nameof(MainViewModel.IsCompact))
            ApplyWindowSize();
    }

    private void ApplyTheme()
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var mode = viewModel.Theme == WindowTheme.Light ? "Light" : "Dark";
        var fluent = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{mode}.xaml");
        if (dictionaries[0].Source != fluent)
            dictionaries[0] = new ResourceDictionary { Source = fluent };
        dictionaries[1] = new ResourceDictionary { Source = new Uri($"/TelPhone;component/Themes/{viewModel.Theme}.xaml", UriKind.Relative) };

        int darkFrame = viewModel.Theme == WindowTheme.Light ? 0 : 1;
        if (SetFrameAttribute(windowHandle, 20, ref darkFrame, sizeof(int)) != 0)
            SetFrameAttribute(windowHandle, 19, ref darkFrame, sizeof(int));
    }

    private void ApplyWindowSize()
    {
        compact = viewModel.IsCompact;
        resizing = true;
        try
        {
            if (compact)
            {
                fullState = WindowState;
                fullBounds = RestoreBounds;
                WindowState = WindowState.Normal;
                ResizeMode = ResizeMode.CanMinimize;
                MinWidth = CompactSize.Width;
                MinHeight = CompactSize.Height;
                Width = CompactSize.Width;
                Height = CompactSize.Height;
            }
            else
            {
                ResizeMode = ResizeMode.CanResize;
                MinWidth = FullMinWidth;
                MinHeight = FullMinHeight;
                Left = fullBounds.Left;
                Top = fullBounds.Top;
                Width = fullBounds.Width;
                Height = fullBounds.Height;
                WindowState = fullState;
            }
        }
        finally
        {
            resizing = false;
        }
        UpdateLayoutMode();
        if (compact)
            NumberInput.Focus();
    }

    private void UpdateLayoutMode()
    {
        if (!IsLoaded || resizing || WindowState == WindowState.Minimized)
            return;

        var wasStacked = stacked;
        stacked = !compact && ActualHeight >= 510 && FillsMonitorHeight();
        if (stacked != wasStacked)
        {
            if (stacked)
            {
                normalTab = Tabs.SelectedItem as TabItem;
                Tabs.SelectedItem = ContactsTab;
            }
            else if (normalTab is not null)
            {
                Tabs.SelectedItem = normalTab;
            }
        }

        Frame.Margin = compact ? new Thickness(10, 8, 10, 5) : new Thickness(16, 12, 16, 12);
        CompactToggle.Content = compact ? "Полный" : "Мини";
        Setup.Visibility = KeypadScroller.Visibility = ClearNumber.Visibility = Tabs.Visibility = Shown(!compact);
        PhoneTab.Visibility = Shown(!stacked);
        Grid.SetRow(DialPanel, stacked ? 0 : 1);
        Grid.SetRow(Tabs, stacked ? 1 : 0);
        Grid.SetRow(ContactsPanel, stacked ? 2 : 1);
        Grid.SetRow(HistoryPanel, stacked ? 2 : 1);
        ContentRow.Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        BottomRow.Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DialPanel.MaxHeight = stacked ? Math.Clamp(ActualHeight * .53, 165, 302) : double.PositiveInfinity;
        UpdatePanels();
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && e.Source == Tabs)
            UpdatePanels();
    }

    private void UpdatePanels()
    {
        DialPanel.Visibility = Shown(compact || stacked || Tabs.SelectedItem == PhoneTab);
        ContactsPanel.Visibility = Shown(!compact && Tabs.SelectedItem == ContactsTab);
        HistoryPanel.Visibility = Shown(!compact && Tabs.SelectedItem == HistoryTab);
    }

    private void CallContactOnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { DataContext: Contact contact } ||
            !viewModel.CallContact.CanExecute(contact))
            return;

        e.Handled = true;
        viewModel.CallContact.Execute(contact);
        ShowDialer(sender, e);
    }

    private void ShowDialer(object sender, RoutedEventArgs e)
    {
        if (stacked)
            return;
        // The button command reads the selected row before the view changes.
        Dispatcher.InvokeAsync(() =>
        {
            if (!stacked)
            {
                Tabs.SelectedItem = PhoneTab;
                NumberInput.Focus();
            }
        });
    }

    private bool FillsMonitorHeight()
    {
        if (WindowState == WindowState.Maximized)
            return true;
        var monitor = MonitorFromWindow(windowHandle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info) && GetWindowRect(windowHandle, out var bounds))
            return bounds.Bottom - bounds.Top >= (info.Work.Bottom - info.Work.Top) * .9;
        return ActualHeight >= SystemParameters.WorkArea.Height * .9;
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int SetFrameAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
}
