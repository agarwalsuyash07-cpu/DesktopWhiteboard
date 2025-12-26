using System;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;   
using System.Windows.Input;      
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;




namespace DesktopWhiteboard
{
    public partial class MainWindow : Window
    {
        private bool isEditMode = false;

        private static Mutex? appMutex;

        private bool isEraserMode = false;
        private Color currentInkColor = Color.FromRgb(30, 30, 30);
        private byte backgroundOpacity = 20; // 0–60 recommended


        private readonly string? savePath;
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;

        private void EnableClickThrough(bool enable)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int styles = GetWindowLong(hwnd, GWL_EXSTYLE);

            if (enable)
                SetWindowLong(hwnd, GWL_EXSTYLE, styles | WS_EX_TRANSPARENT);
            else
                SetWindowLong(hwnd, GWL_EXSTYLE, styles & ~WS_EX_TRANSPARENT);
        }
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 1;
        private const int MOD_ALT = 0x1;
        private const int MOD_CTRL = 0x2;
        private const int VK_W = 0x57; // W key
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            RegisterHotKey(hwnd, HOTKEY_ID, MOD_CTRL | MOD_ALT, VK_W);

            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source.AddHook(WndProc);
        }
        private enum CanvasTheme
        {
            Light,
            Dark
        }

        private CanvasTheme currentTheme = CanvasTheme.Light;


        private void HideEditor()
        {
            Topmost = false;
            WindowState = WindowState.Minimized;
            Hide();
        }
        private void SetAsWallpaper()
        {
            try
            {
                string imagePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DesktopWhiteboard",
                    "wallpaper.png");

                if (!File.Exists(imagePath))
                    return;
                ForceWallpaperStyle();
                SystemParametersInfo(
                    SPI_SETDESKWALLPAPER,
                    0,
                    imagePath,
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch
            {
                // never interrupt user
            }
        }
        private const int SPI_SETDESKWALLPAPER = 20;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDCHANGE = 0x02;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(
            int uAction,
            int uParam,
            string lpvParam,
            int fuWinIni);


        private void RenderToImage()
        {
            try
            {
                int width = (int)SystemParameters.PrimaryScreenWidth;
                int height = (int)SystemParameters.PrimaryScreenHeight;

                // Create a clean visual
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    // Background
                    if (currentTheme == CanvasTheme.Light)
                    {
                        dc.DrawRectangle(
                            new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                            null,
                            new Rect(0, 0, width, height));
                    }
                    else
                    {
                        dc.DrawRectangle(
                            new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                            null,
                            new Rect(0, 0, width, height));
                    }

                    // Draw strokes ONLY ONCE
                    Whiteboard.Strokes.Draw(dc);
                }

                var rtb = new RenderTargetBitmap(
                    width, height, 96, 96, PixelFormats.Pbgra32);

                rtb.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                string imagePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DesktopWhiteboard",
                    "wallpaper.png");

                using FileStream fs = new FileStream(imagePath, FileMode.Create);
                encoder.Save(fs);
            }
            catch { }
        }

        private void ForceWallpaperStyle()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\Desktop", true);

                if (key == null)
                    return;

                key.SetValue("WallpaperStyle", "2"); // Fill
                key.SetValue("TileWallpaper", "0");
            }
            catch
            {
                // ignore
            }
        }

        public MainWindow()
        {
            appMutex = new Mutex(true, "DesktopWhiteboardSingleton", out bool isNew);

            if (!isNew)
            {
                Application.Current.Shutdown();
                return;
            }

            InitializeComponent();

            savePath = GetSavePath();

            ConfigureInk();
            LoadStrokes();

            EnableClickThrough(true);

            // IMPORTANT: show once to finalize HWND state
            Show();
            Hide();
        }




        private void ClearAllInk()
        {
            Whiteboard.Strokes.Clear();
        }

        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (!isEditMode)
                return;

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                await ExitEditModeAsync();
            }
            else if (e.Key == Key.E)
            {
                ToggleEraser();
            }
            else if (e.Key == Key.I)
            {
                isEraserMode = false;
                Whiteboard.EditingMode = InkCanvasEditingMode.Ink;
                Cursor = Cursors.Pen;
            }
            else if (e.Key == Key.C &&
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift))
                    == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                ClearAllInk();
            }
        }




        private IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;

            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                handled = true;

                if (isEditMode)
                    _ = ExitEditModeAsync();
                else
                    EnterEditMode();
            }

            return IntPtr.Zero;
        }
        private void ApplyCanvasTheme()
        {
            if (currentTheme == CanvasTheme.Light)
            {
                Whiteboard.Background = new SolidColorBrush(
                    Color.FromArgb(backgroundOpacity, 255, 255, 255));
            }
            else
            {
                Whiteboard.Background = new SolidColorBrush(
                    Color.FromArgb(backgroundOpacity, 30, 30, 30));
            }
        }

        private void LightTheme_Checked(object sender, RoutedEventArgs e)
        {
            currentTheme = CanvasTheme.Light;
            ApplyCanvasTheme();
            UpdateWallpaperLive();
        }


        private void DarkTheme_Checked(object sender, RoutedEventArgs e)
        {
            currentTheme = CanvasTheme.Dark;
            ApplyCanvasTheme();
            UpdateWallpaperLive();
        }

        private void PenThickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var ink = Whiteboard.DefaultDrawingAttributes.Clone();
            ink.Width = e.NewValue;
            ink.Height = e.NewValue;
            Whiteboard.DefaultDrawingAttributes = ink;
        }



        protected override void OnClosed(EventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ID);
            base.OnClosed(e);
        }
        private void ToggleEraser()
        {
            isEraserMode = !isEraserMode;

            Whiteboard.EditingMode = isEraserMode
                ? InkCanvasEditingMode.EraseByStroke
                : InkCanvasEditingMode.Ink;
            Cursor = isEraserMode
                ? System.Windows.Input.Cursors.Cross
                : System.Windows.Input.Cursors.Pen;

        }
        private void SetInkColor(Color color)
        {
            currentInkColor = color;

            var ink = Whiteboard.DefaultDrawingAttributes.Clone();
            ink.Color = color;

            Whiteboard.DefaultDrawingAttributes = ink;
        }
        private void UpdateBackground()
        {
            ApplyCanvasTheme();
        }
        private async void UpdateWallpaperLive()
        {
            RenderToImage();
            await Task.Delay(100);
            SetAsWallpaper();
        }



        private void InkColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Background is SolidColorBrush brush)
            {
                SetInkColor(brush.Color);
            }
        }
        private void Pen_Click(object sender, RoutedEventArgs e)
        {
            isEraserMode = false;
            Whiteboard.EditingMode = InkCanvasEditingMode.Ink;
            Cursor = Cursors.Pen;
        }

        private void Eraser_Click(object sender, RoutedEventArgs e)
        {
            isEraserMode = true;
            Whiteboard.EditingMode = InkCanvasEditingMode.EraseByStroke;
            Cursor = Cursors.Cross;
        }
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Whiteboard.Strokes.Clear();
        }
        private void BgMinus_Click(object sender, RoutedEventArgs e)
        {
            backgroundOpacity = (byte)Math.Max(0, backgroundOpacity - 5);
            UpdateBackground();
            UpdateWallpaperLive();
        }

        private void BgPlus_Click(object sender, RoutedEventArgs e)
        {
            backgroundOpacity = (byte)Math.Min(60, backgroundOpacity + 5);
            UpdateBackground();
            UpdateWallpaperLive();
        }
        private void EnterEditMode()
        {
            isEditMode = true;

            Show();
            Activate();
            Focus();

            EnableClickThrough(false);
            ApplyCanvasTheme();

            UpdateBackground();
            Cursor = Cursors.Pen;
            Whiteboard.IsHitTestVisible = true;

            OverlayMenu.Visibility = Visibility.Visible;
        }


        private async Task ExitEditModeAsync()
        {
            isEditMode = false;

            SaveStrokes();
            RenderToImage();
            await Task.Delay(150);
            SetAsWallpaper();

            EnableClickThrough(true);

            // IMPORTANT: make canvas fully transparent AFTER rendering
            Whiteboard.Background = Brushes.Transparent;

            Cursor = Cursors.Arrow;
            OverlayMenu.Visibility = Visibility.Collapsed;

            Hide();
        }



        

        private void ConfigureInk()
        {
            var ink = new DrawingAttributes
            {
                Color = currentInkColor,
                Width = 3.2,
                Height = 3.2,
                FitToCurve = false,
                IgnorePressure = true
            };

            Whiteboard.DefaultDrawingAttributes = ink;
            Whiteboard.EditingMode = InkCanvasEditingMode.Ink;
            Whiteboard.EditingModeInverted = InkCanvasEditingMode.EraseByStroke;

            // Disable noisy Windows pen features
            Stylus.SetIsPressAndHoldEnabled(Whiteboard, false);
            Stylus.SetIsFlicksEnabled(Whiteboard, false);
            Stylus.SetIsTapFeedbackEnabled(Whiteboard, false);
            Stylus.SetIsTouchFeedbackEnabled(Whiteboard, false);

            // Block touch input
            Whiteboard.TouchDown += (_, e) => e.Handled = true;
            Whiteboard.TouchMove += (_, e) => e.Handled = true;
            Whiteboard.TouchUp += (_, e) => e.Handled = true;
        }


        private string GetSavePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopWhiteboard");

            Directory.CreateDirectory(dir);

            return Path.Combine(dir, "strokes.isf");
        }

        private void SaveStrokes()
        {
            if (savePath is null)
                return;

            try
            {
                using FileStream fs = new FileStream(savePath, FileMode.Create);
                Whiteboard.Strokes.Save(fs);
            }
            catch
            {
                // silently fail (never interrupt user)
            }
        }


        private void LoadStrokes()
        {
            if (savePath is null || !File.Exists(savePath))
                return;

            try
            {
                using FileStream fs = new FileStream(savePath, FileMode.Open);
                Whiteboard.Strokes = new StrokeCollection(fs);
            }
            catch
            {
                // corrupted file? just ignore
            }
        }

    }
}
