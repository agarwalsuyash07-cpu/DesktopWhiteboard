using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
namespace DesktopWhiteboard
{
    public partial class MainWindow : Window
    {
        // ═══════════════════════════════════════
        //  Constants
        // ═══════════════════════════════════════

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int HOTKEY_ID = 1;
        private const int MOD_ALT = 0x1;
        private const int MOD_CTRL = 0x2;
        private const int VK_W = 0x57; // W key

        private const double HighlighterScale = 4.5;
        private const int SPI_SETDESKWALLPAPER = 20;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDCHANGE = 0x02;

        // ═══════════════════════════════════════
        //  Win32 Imports
        // ═══════════════════════════════════════

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);


        // ═══════════════════════════════════════
        //  Enum
        // ═══════════════════════════════════════

        private enum CanvasTheme { Light, Dark }

        // ═══════════════════════════════════════
        //  Fields
        // ═══════════════════════════════════════

        // Singleton
        private static Mutex? appMutex;

        // Mode flags
        private bool isEditMode;
        private bool isEraserMode;
        private bool isHighlighterMode;
        private bool isTextMode;
        private bool isStartupHelpVisible = true;

        // Drawing state
        private Color currentInkColor = Color.FromRgb(30, 30, 30);
        private Color currentHighlighterColor = Color.FromArgb(120, 255, 255, 0);
        private double penThickness = 3;
        private double highlighterThickness = 4;
        private byte backgroundOpacity = 20;
        private Button? activeColorButton;

        // Text state
        private TextBox? activeTextBox;
        private TextBox? selectedTextBox;
        private double currentTextSize = 24;
        private bool isBold;
        private bool isItalic;
        private bool isUnderline;

        // Text dragging
        private bool isDraggingText;
        private TextBox? draggedText;
        private Point dragStart;

        // Image dragging
        private bool isDraggingImage;
        private Border? draggedImage;
        private Point imageDragStart;

        // Undo / Redo
        private readonly Stack<StrokeCollection> undoStrokes = new();
        private readonly Stack<StrokeCollection> redoStrokes = new();
        private StrokeCollection? lastStrokeSnapshot;
        private bool isRestoringInkHistory;

        // Theme & persistence
        private CanvasTheme currentTheme = CanvasTheme.Light;
        private readonly string? savePath;
        private string? originalWallpaperPath;

        // ═══════════════════════════════════════
        //  Win32 Helpers
        // ═══════════════════════════════════════

        private void EnableClickThrough(bool enable)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int styles = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (enable)
                SetWindowLong(hwnd, GWL_EXSTYLE, styles | WS_EX_TRANSPARENT);
            else
                SetWindowLong(hwnd, GWL_EXSTYLE, styles & ~WS_EX_TRANSPARENT);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            RegisterHotKey(hwnd, HOTKEY_ID, MOD_CTRL | MOD_ALT, VK_W);
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source.AddHook(WndProc);
        }

        // ═══════════════════════════════════════
        //  Wallpaper Integration
        // ═══════════════════════════════════════

        private string OriginalWallpaperFile =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopWhiteboard", "original_wallpaper.txt");

        private string CompositeWallpaperFile =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopWhiteboard", "wallpaper.png");

        /// <summary>
        /// Saves the current desktop wallpaper path so we can composite onto it.
        /// Only saves once — won't overwrite if already stored.
        /// </summary>
        private void SaveOriginalWallpaper()
        {
            try
            {
                if (File.Exists(OriginalWallpaperFile))
                {
                    originalWallpaperPath = File.ReadAllText(OriginalWallpaperFile).Trim();
                    return;
                }
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\Desktop");
                string? wallpaper = key?.GetValue("Wallpaper") as string;
                if (!string.IsNullOrEmpty(wallpaper))
                {
                    originalWallpaperPath = wallpaper;
                    Directory.CreateDirectory(Path.GetDirectoryName(OriginalWallpaperFile)!);
                    File.WriteAllText(OriginalWallpaperFile, wallpaper);
                }
            }
            catch { }
        }

        /// <summary>
        /// Restores the user's original wallpaper (without drawings).
        /// Called when entering edit mode so strokes don't appear doubled.
        /// </summary>
        private void RestoreOriginalWallpaper()
        {
            try
            {
                if (string.IsNullOrEmpty(originalWallpaperPath) ||
                    !File.Exists(originalWallpaperPath))
                    return;

                ForceWallpaperStyle();
                SystemParametersInfo(
                    SPI_SETDESKWALLPAPER, 0,
                    originalWallpaperPath,
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch { }
        }

        /// <summary>
        /// Renders strokes + text on top of the user's original wallpaper image.
        /// </summary>
        private void RenderToImage()
        {
            try
            {
                int width = (int)SystemParameters.PrimaryScreenWidth;
                int height = (int)SystemParameters.PrimaryScreenHeight;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    // Draw the user's original wallpaper as background
                    if (!string.IsNullOrEmpty(originalWallpaperPath) &&
                        File.Exists(originalWallpaperPath))
                    {
                        var bgImage = new BitmapImage();
                        bgImage.BeginInit();
                        bgImage.UriSource = new Uri(originalWallpaperPath);
                        bgImage.CacheOption = BitmapCacheOption.OnLoad;
                        bgImage.EndInit();
                        dc.DrawImage(bgImage, new Rect(0, 0, width, height));
                    }
                    else
                    {
                        // Fallback: solid color if no wallpaper found
                        var bg = currentTheme == CanvasTheme.Light
                            ? new SolidColorBrush(Colors.White)
                            : new SolidColorBrush(Color.FromRgb(30, 30, 30));
                        dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
                    }

                    // Draw strokes
                    Whiteboard.Strokes.Draw(dc);

                    // Draw text
                    foreach (UIElement element in TextLayer.Children)
                    {
                        if (element is TextBox tb)
                        {
                            var formattedText = new FormattedText(
                                tb.Text,
                                System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight,
                                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                                tb.FontSize,
                                tb.Foreground,
                                VisualTreeHelper.GetDpi(this).PixelsPerDip);
                            double x = Canvas.GetLeft(tb);
                            double y = Canvas.GetTop(tb);
                            dc.DrawText(formattedText, new Point(x, y));
                        }
                    }
                }

                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                Directory.CreateDirectory(Path.GetDirectoryName(CompositeWallpaperFile)!);
                using var fs = new FileStream(CompositeWallpaperFile, FileMode.Create);
                encoder.Save(fs);
            }
            catch { }
        }

        private void SetAsWallpaper()
        {
            try
            {
                if (!File.Exists(CompositeWallpaperFile))
                    return;
                ForceWallpaperStyle();
                SystemParametersInfo(
                    SPI_SETDESKWALLPAPER, 0,
                    CompositeWallpaperFile,
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch { }
        }

        private void ForceWallpaperStyle()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\Desktop", true);
                if (key == null) return;
                key.SetValue("WallpaperStyle", "10"); // Fill
                key.SetValue("TileWallpaper", "0");
            }
            catch { }
        }


        // ═══════════════════════════════════════
        //  Undo / Redo
        // ═══════════════════════════════════════

        private void SaveUndoSnapshot()
        {
            undoStrokes.Push(Whiteboard.Strokes.Clone());
            redoStrokes.Clear();
        }

        private void Undo()
        {
            if (undoStrokes.Count == 0)
                return;

            isRestoringInkHistory = true;

            redoStrokes.Push(Whiteboard.Strokes.Clone());
            Whiteboard.Strokes = undoStrokes.Pop();

            lastStrokeSnapshot = Whiteboard.Strokes.Clone();
            isRestoringInkHistory = false;
        }

        private void Redo()
        {
            if (redoStrokes.Count == 0)
                return;

            isRestoringInkHistory = true;

            undoStrokes.Push(Whiteboard.Strokes.Clone());
            Whiteboard.Strokes = redoStrokes.Pop();

            lastStrokeSnapshot = Whiteboard.Strokes.Clone();
            isRestoringInkHistory = false;
        }
        // ═══════════════════════════════════════
        //  Text Tool
        // ═══════════════════════════════════════

        private void TextLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isTextMode)
                return;
            var pos = e.GetPosition(TextLayer);
            CreateTextBox(pos);
        }
        private void ActivateTextBox(TextBox tb)
        {
            if (activeTextBox != null && activeTextBox != tb)
                FinalizeTextBox(activeTextBox);

            activeTextBox = tb;
            selectedTextBox = tb;

            tb.IsReadOnly = false;
            tb.Focus();
            tb.SelectAll();

            // 🔥 THIS IS THE CRITICAL LINE
            SyncFormattingButtons(tb);
        }
        private void FinalizeTextBox(TextBox tb)
        {
            if (string.IsNullOrWhiteSpace(tb.Text))
            {
                TextLayer.Children.Remove(tb);
            }
            else
            {
                tb.IsReadOnly = true;
                tb.BorderThickness = new Thickness(0);
            }

            if (activeTextBox == tb)
                activeTextBox = null;

            selectedTextBox = null;
            ClearFormattingIndicators();
        }
        private void Text_MouseDown(object sender, MouseButtonEventArgs e)
        {
            draggedText = sender as TextBox;
            dragStart = e.GetPosition(TextLayer);
            isDraggingText = true;
            if (draggedText == null) return;
            draggedText.CaptureMouse();
        }
        private void Text_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDraggingText || draggedText == null) return;
            Point current = e.GetPosition(TextLayer);
            double dx = current.X - dragStart.X;
            double dy = current.Y - dragStart.Y;
            Canvas.SetLeft(draggedText, Canvas.GetLeft(draggedText) + dx);
            Canvas.SetTop(draggedText, Canvas.GetTop(draggedText) + dy);
            dragStart = current;
        }
        private void Text_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (draggedText == null) return;
            draggedText.ReleaseMouseCapture();
            isDraggingText = false;
            var tb = draggedText;
            double finalX = Canvas.GetLeft(tb);
            double finalY = Canvas.GetTop(tb);
            // Optional: capture previous position if you want real undo later
            draggedText = null;
        }
        private void CreateTextBox(Point p)
        {
            var tb = new TextBox
            {
                Text = "",
                FontSize = 24,
                Foreground = new SolidColorBrush(currentInkColor),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                AcceptsReturn = false,
                Focusable = true,
                MinWidth = 100,
            };
            tb.FontSize = currentTextSize;
            tb.FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal;
            tb.FontStyle = isItalic ? FontStyles.Italic : FontStyles.Normal;
            tb.TextDecorations = isUnderline ? TextDecorations.Underline : null;
            tb.Cursor = Cursors.SizeAll;
            tb.PreviewMouseLeftButtonDown += Text_MouseDown;
            tb.PreviewMouseMove += Text_MouseMove;
            tb.PreviewMouseLeftButtonUp += Text_MouseUp;
            tb.PreviewMouseDown += (s, e) =>
            {
                selectedTextBox = tb;
                e.Handled = false;
            };
            Canvas.SetLeft(tb, p.X);
            Canvas.SetTop(tb, p.Y);
            tb.PreviewMouseDown += (s, e) =>
            {
                e.Handled = true;
                ActivateTextBox(tb);
            };
            TextLayer.Children.Add(tb);
            ActivateTextBox(tb);
        }
        private void SyncFormattingButtons(TextBox tb)
        {
            BoldButton.IsChecked = tb.FontWeight == FontWeights.Bold;
            ItalicButton.IsChecked = tb.FontStyle == FontStyles.Italic;
            UnderlineButton.IsChecked = tb.TextDecorations == TextDecorations.Underline;
        }
        private void TextSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            currentTextSize = e.NewValue;
            ApplyTextFormatting();
        }
        private void Bold_Click(object sender, RoutedEventArgs e)
        {
            if (selectedTextBox == null)
            {
                // User is setting default for future text
                isBold = !isBold;
                return;
            }

            // User is editing EXISTING text → do NOT touch defaults
            selectedTextBox.FontWeight =
                selectedTextBox.FontWeight == FontWeights.Bold
                    ? FontWeights.Normal
                    : FontWeights.Bold;

            SyncFormattingButtons(selectedTextBox);
        }
        private void Italic_Click(object sender, RoutedEventArgs e)
        {
            if (selectedTextBox == null)
            {
                isItalic = !isItalic; // default only
                return;
            }

            selectedTextBox.FontStyle =
                selectedTextBox.FontStyle == FontStyles.Italic
                    ? FontStyles.Normal
                    : FontStyles.Italic;

            SyncFormattingButtons(selectedTextBox);
        }
        private void Underline_Click(object sender, RoutedEventArgs e)
        {
            if (selectedTextBox == null)
            {
                isUnderline = !isUnderline; // default only
                return;
            }

            selectedTextBox.TextDecorations =
                selectedTextBox.TextDecorations == TextDecorations.Underline
                    ? null
                    : TextDecorations.Underline;

            SyncFormattingButtons(selectedTextBox);
        }
        private void InitializeThemeAndDefaults()
        {
            bool isDark = IsSystemDarkTheme();

            currentTheme = isDark ? CanvasTheme.Dark : CanvasTheme.Light;

            // Set default ink color based on theme
            currentInkColor = isDark ? Colors.White : Colors.Black;

            ApplyCanvasTheme();

            var ink = Whiteboard.DefaultDrawingAttributes.Clone();
            ink.Color = currentInkColor;
            Whiteboard.DefaultDrawingAttributes = ink;
        }
        private bool IsSystemDarkTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

                if (key?.GetValue("AppsUseLightTheme") is int value)
                {
                    return value == 0; // 0 = dark, 1 = light
                }
            }
            catch { }

            return false; // default to light
        }
        private void ApplyTextFormatting()
        {
            if (selectedTextBox == null) return;
            selectedTextBox.FontSize = currentTextSize;
            selectedTextBox.FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal;
            selectedTextBox.FontStyle = isItalic ? FontStyles.Italic : FontStyles.Normal;
            selectedTextBox.TextDecorations =
                isUnderline ? TextDecorations.Underline : null;
        }
        // ═══════════════════════════════════════
        //  Keyboard Handling
        // ═══════════════════════════════════════

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // =========================
            // HELP OVERLAY
            // =========================
            if (e.Key == Key.F1)
            {
                e.Handled = true;
                ShowHelpOverlay();
                return;
            }
            if (HelpOverlay.Visibility == Visibility.Visible && e.Key == Key.Escape)
            {
                e.Handled = true;
                HelpOverlay.Visibility = Visibility.Collapsed;
                if (!isEditMode)
                    Hide();
                return;
            }
            // =========================
            // TEXT TOOL SHORTCUTS
            // =========================
            if (e.Key == Key.Delete && activeTextBox != null)
            {
                TextLayer.Children.Remove(activeTextBox);
                activeTextBox = null;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && activeTextBox != null)
            {
                FinalizeTextBox(activeTextBox);
                e.Handled = true;
                return;
            }

        }
        private void SetActiveTool(Button active)
        {
            PenButton.Background = new SolidColorBrush(Color.FromRgb(42, 42, 42));
            EraserButton.Background = new SolidColorBrush(Color.FromRgb(42, 42, 42));
            HighlighterButton.Background = new SolidColorBrush(Color.FromRgb(42, 42, 42));
            TextButton.Background = new SolidColorBrush(Color.FromRgb(42, 42, 42));
            active.Background = new SolidColorBrush(Color.FromRgb(70, 70, 70));
        }

        // ═══════════════════════════════════════
        //  UI Helpers
        // ═══════════════════════════════════════

        private void FadeIn(UIElement element)
        {
            element.Visibility = Visibility.Visible;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
            };
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        private void FadeOut(UIElement element)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
            };
            anim.Completed += (_, __) =>
            {
                element.Visibility = Visibility.Collapsed;
            };
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        // ═══════════════════════════════════════
        //  Constructor & Initialization
        // ═══════════════════════════════════════

        public MainWindow()
        {
            appMutex = new Mutex(true, "DesktopWhiteboardSingleton", out bool isNew);
            if (!isNew)
            {
                Application.Current.Shutdown();
                return;
            }
            InitializeComponent();
            InitializeThemeAndDefaults(); // 🔥 MUST be before ConfigureInk
            TextLayer.MouseLeftButtonDown += TextLayer_MouseLeftButtonDown;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            savePath = GetSavePath();
            ConfigureInk();
            LoadTextLayer();
            LoadStrokes();
            lastStrokeSnapshot = Whiteboard.Strokes.Clone();
            Whiteboard.Strokes.StrokesChanged += (s, e) =>
            {
                if (isRestoringInkHistory)
                    return;

                if (lastStrokeSnapshot != null)
                    undoStrokes.Push(lastStrokeSnapshot);

                redoStrokes.Clear();

                // Update snapshot AFTER change
                lastStrokeSnapshot = Whiteboard.Strokes.Clone();
            };
            SaveStrokes();

            // Save user's original wallpaper, then bake drawings into it
            SaveOriginalWallpaper();
            RenderToImage();
            SetAsWallpaper();

            EnableClickThrough(true);
            isEditMode = false;
            ShowInTaskbar = false;
            Show();   // Triggers OnSourceInitialized → registers hotkey
            Hide();   // Then hide — drawings live in wallpaper
        }
        private void ClearAllInk()
        {
            SaveUndoSnapshot();
            Whiteboard.Strokes.Clear();
        }

        private class SavedText
        {
            public string Text { get; set; } = "";
            public double X { get; set; }
            public double Y { get; set; }
            public double FontSize { get; set; }
            public bool Bold { get; set; }
            public bool Italic { get; set; }
            public bool Underline { get; set; }
            public Color Color { get; set; }
        }
        // ═══════════════════════════════════════
        //  Data Persistence
        // ═══════════════════════════════════════

        private void SaveTextLayer()
        {
            try
            {
                var list = new List<SavedText>();

                foreach (UIElement el in TextLayer.Children)
                {
                    if (el is not TextBox tb)
                        continue;

                    list.Add(new SavedText
                    {
                        Text = tb.Text,
                        X = Canvas.GetLeft(tb),
                        Y = Canvas.GetTop(tb),
                        FontSize = tb.FontSize,
                        Bold = tb.FontWeight == FontWeights.Bold,
                        Italic = tb.FontStyle == FontStyles.Italic,
                        Underline = tb.TextDecorations == TextDecorations.Underline,
                        Color = ((SolidColorBrush)tb.Foreground).Color
                    });
                }

                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DesktopWhiteboard",
                    "text.json");

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                File.WriteAllText(path,
                    System.Text.Json.JsonSerializer.Serialize(list));
            }
            catch { }
        }
        private void LoadTextLayer()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DesktopWhiteboard",
                    "text.json");

                if (!File.Exists(path))
                    return;

                var list = System.Text.Json.JsonSerializer.Deserialize<List<SavedText>>(
                    File.ReadAllText(path));

                if (list == null)
                    return;

                TextLayer.Children.Clear();

                foreach (var item in list)
                {
                    var tb = new TextBox
                    {
                        Text = item.Text,
                        FontSize = item.FontSize,
                        Foreground = new SolidColorBrush(item.Color),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        IsReadOnly = true
                    };

                    tb.FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal;
                    tb.FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal;
                    tb.TextDecorations = item.Underline ? TextDecorations.Underline : null;

                    Canvas.SetLeft(tb, item.X);
                    Canvas.SetTop(tb, item.Y);

                    // reattach handlers
                    tb.PreviewMouseLeftButtonDown += Text_MouseDown;
                    tb.PreviewMouseMove += Text_MouseMove;
                    tb.PreviewMouseLeftButtonUp += Text_MouseUp;
                    tb.PreviewMouseDown += (s, e) =>
                    {
                        e.Handled = true;
                        ActivateTextBox(tb);
                    };

                    TextLayer.Children.Add(tb);
                }
            }
            catch { }
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
            else if (e.Key == Key.Z &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                Undo();
            }
            else if (e.Key == Key.Y &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                Redo();
            }
            else if (e.Key == Key.V &&
                    (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                PasteImageFromClipboard();
                e.Handled = true;
            }

        }
        // ═══════════════════════════════════════
        //  Image Paste & Drag
        // ═══════════════════════════════════════

        private void PasteImageFromClipboard()
        {
            if (!isEditMode)
                return;

            if (!Clipboard.ContainsImage())
                return;

            BitmapSource? source = Clipboard.GetImage();
            if (source == null)
                return;

            var image = new Image
            {
                Source = source,
                Stretch = Stretch.Fill
            };

            var container = CreateResizableImage(image);

            Canvas.SetLeft(container, 200);
            Canvas.SetTop(container, 200);

            ImageLayer.Children.Add(container);
            ImageLayer.IsHitTestVisible = true;
        }
        private void Image_MouseDown(object sender, MouseButtonEventArgs e)
        {
            draggedImage = sender as Border;
            if (draggedImage == null) return;

            imageDragStart = e.GetPosition(ImageLayer);
            isDraggingImage = true;
            draggedImage.CaptureMouse();
        }

        private void Image_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDraggingImage || draggedImage == null) return;

            Point current = e.GetPosition(ImageLayer);
            double dx = current.X - imageDragStart.X;
            double dy = current.Y - imageDragStart.Y;

            Canvas.SetLeft(draggedImage, Canvas.GetLeft(draggedImage) + dx);
            Canvas.SetTop(draggedImage, Canvas.GetTop(draggedImage) + dy);

            imageDragStart = current;
        }

        private void Image_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (draggedImage == null) return;

            draggedImage.ReleaseMouseCapture();
            draggedImage = null;
            isDraggingImage = false;
        }
        private Border CreateResizableImage(Image image)
        {
            var border = new Border
            {
                BorderBrush = Brushes.DodgerBlue,
                BorderThickness = new Thickness(1),
                Width = 300,
                Height = 200,
                Child = image
            };

            border.MouseLeftButtonDown += Image_MouseDown;
            border.MouseMove += Image_MouseMove;
            border.MouseLeftButtonUp += Image_MouseUp;

            // Resize handle (bottom-right)
            var thumb = new System.Windows.Controls.Primitives.Thumb
            {
                Width = 12,
                Height = 12,
                Cursor = Cursors.SizeNWSE,
                Background = Brushes.White,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1)
            };

            thumb.DragDelta += (s, e) =>
            {
                border.Width = Math.Max(50, border.Width + e.HorizontalChange);
                border.Height = Math.Max(50, border.Height + e.VerticalChange);
            };

            var grid = new Grid();
            grid.Children.Add(image);
            grid.Children.Add(thumb);

            Grid.SetRow(thumb, 1);
            Grid.SetColumn(thumb, 1);
            thumb.HorizontalAlignment = HorizontalAlignment.Right;
            thumb.VerticalAlignment = VerticalAlignment.Bottom;

            border.Child = grid;
            return border;
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
        // ═══════════════════════════════════════
        //  Theme
        // ═══════════════════════════════════════

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
        }
        private void DarkTheme_Checked(object sender, RoutedEventArgs e)
        {
            currentTheme = CanvasTheme.Dark;
            ApplyCanvasTheme();
        }
        private void ClearFormattingIndicators()
        {
            BoldButton.IsChecked = false;
            ItalicButton.IsChecked = false;
            UnderlineButton.IsChecked = false;
        }
        private void PenThickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isHighlighterMode)
            {
                highlighterThickness = e.NewValue;

                var ink = Whiteboard.DefaultDrawingAttributes.Clone();
                ink.Width = e.NewValue * HighlighterScale;
                ink.Height = e.NewValue * HighlighterScale;
                Whiteboard.DefaultDrawingAttributes = ink;
            }
            else
            {
                penThickness = e.NewValue;

                var ink = Whiteboard.DefaultDrawingAttributes.Clone();
                ink.Width = e.NewValue;
                ink.Height = e.NewValue;
                Whiteboard.DefaultDrawingAttributes = ink;
            }
        }
        // ═══════════════════════════════════════
        //  Window Lifecycle
        // ═══════════════════════════════════════

        protected override void OnClosed(EventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ID);
            base.OnClosed(e);
            SaveTextLayer();
            SaveStrokes();
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


        private void SetActiveColorButton(Button btn)
        {
            // Reset previous
            if (activeColorButton != null)
            {
                activeColorButton.BorderThickness = new Thickness(1);
                activeColorButton.BorderBrush = Brushes.Gray;
            }

            // Highlight current
            btn.BorderThickness = new Thickness(3);
            btn.BorderBrush = Brushes.White;

            activeColorButton = btn;
        }
        private void InkColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Background is not SolidColorBrush brush)
                return;

            if (isHighlighterMode)
            {
                // Store logical highlighter color (force transparency)
                currentHighlighterColor = Color.FromArgb(120, brush.Color.R, brush.Color.G, brush.Color.B);

                var ink = Whiteboard.DefaultDrawingAttributes.Clone();
                ink.Color = currentHighlighterColor;
                Whiteboard.DefaultDrawingAttributes = ink;
            }
            else
            {
                currentInkColor = brush.Color;

                var ink = Whiteboard.DefaultDrawingAttributes.Clone();
                ink.Color = currentInkColor;
                Whiteboard.DefaultDrawingAttributes = ink;
            }

            SetActiveColorButton(btn);
        }
        // ═══════════════════════════════════════
        //  Drawing Tools
        // ═══════════════════════════════════════

        private void Pen_Click(object sender, RoutedEventArgs e)
        {
            selectedTextBox = null;
            ClearFormattingIndicators();
            isHighlighterMode = false;
            isEraserMode = false;
            ExitTextMode();
            SetActiveTool(PenButton);

            var ink = new DrawingAttributes
            {
                Color = currentInkColor,
                Width = penThickness,
                Height = penThickness,
                IsHighlighter = false,
                IgnorePressure = true
            };

            Whiteboard.DefaultDrawingAttributes = ink;
            Whiteboard.EditingMode = InkCanvasEditingMode.Ink;
            Cursor = Cursors.Pen;
            // Restore color UI highlight
            if (activeColorButton != null)
            {
                SetActiveColorButton(activeColorButton);
            }
        }
        private void Highlighter_Click(object sender, RoutedEventArgs e)
        {
            selectedTextBox = null;
            ClearFormattingIndicators();
            ExitTextMode();
            isHighlighterMode = true;
            isEraserMode = false;

            var ink = new DrawingAttributes
            {
                Color = currentHighlighterColor,
                Width = highlighterThickness * HighlighterScale,
                Height = highlighterThickness * HighlighterScale,
                IsHighlighter = true,
                IgnorePressure = true
            };

            Whiteboard.DefaultDrawingAttributes = ink;
            Whiteboard.EditingMode = InkCanvasEditingMode.Ink;
            Cursor = Cursors.Pen;
            SetActiveTool(HighlighterButton);
        }
        private void Eraser_Click(object sender, RoutedEventArgs e)
        {
            selectedTextBox = null;
            ClearFormattingIndicators();
            isEraserMode = true;
            isHighlighterMode = false;
            ExitTextMode();
            Whiteboard.EditingMode = InkCanvasEditingMode.EraseByStroke;
            Cursor = Cursors.Cross;
            SetActiveTool(EraserButton);
        }
        private void Text_Click(object sender, RoutedEventArgs e)
        {
            isTextMode = true;
            Whiteboard.EditingMode = InkCanvasEditingMode.None;
            TextLayer.IsHitTestVisible = true;
            Cursor = Cursors.IBeam;
            SetActiveTool(TextButton);
        }
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Whiteboard.Strokes.Clear();
            TextLayer.Children.Clear();
            activeTextBox = null;
        }

        private void CloseHelpOverlay(object sender, RoutedEventArgs e)
        {
            HelpOverlay.Visibility = Visibility.Collapsed;
            isStartupHelpVisible = false;
            // Hide window — drawings live in wallpaper
            Hide();
        }

        private void ExitTextMode()
        {
            isTextMode = false;
            TextLayer.IsHitTestVisible = false;
            Whiteboard.IsHitTestVisible = true;

            if (activeTextBox != null)
                FinalizeTextBox(activeTextBox);

            activeTextBox = null;
            selectedTextBox = null;
            ClearFormattingIndicators();
        }
        private void ResetInkInputState()
        {
            Whiteboard.IsHitTestVisible = false;
            Whiteboard.IsEnabled = false;
            // Force WPF to fully release input
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            Whiteboard.IsEnabled = true;
            Whiteboard.IsHitTestVisible = true;
            Whiteboard.EditingMode = InkCanvasEditingMode.None;
            Whiteboard.EditingMode = InkCanvasEditingMode.Ink;
            Focus();
            Keyboard.Focus(Whiteboard);
        }
        private void ShowHelpOverlay()
        {
            Show();
            Activate();
            Focus();
            EnableClickThrough(false);
            FadeIn(HelpOverlay);
        }
        private void ShowOverlay()
        {
            OverlayMenu.IsHitTestVisible = true;
            var sb = (Storyboard)FindResource("FadeInOverlay");
            sb.Begin(OverlayMenu);
        }
        private async Task HideOverlayAsync()
        {
            OverlayMenu.IsHitTestVisible = false;
            var sb = (Storyboard)FindResource("FadeOutOverlay");
            sb.Begin(OverlayMenu);
            // Wait for animation to finish
            await Task.Delay(150);
        }
        // ═══════════════════════════════════════
        //  Edit Mode Management
        // ═══════════════════════════════════════

        private void EnterEditMode()
        {
            if (isStartupHelpVisible)
            {
                HelpOverlay.Visibility = Visibility.Visible;
            }
            ShowOverlay();
            if (HelpOverlay.Visibility == Visibility.Visible)
                HelpOverlay.Visibility = Visibility.Collapsed;
            isEditMode = true;
            ImageLayer.IsHitTestVisible = true;

            // Cover the baked wallpaper by setting our window background
            // to the ORIGINAL wallpaper image — prevents double-text.
            if (!string.IsNullOrEmpty(originalWallpaperPath) &&
                File.Exists(originalWallpaperPath))
            {
                var bg = new BitmapImage();
                bg.BeginInit();
                bg.UriSource = new Uri(originalWallpaperPath);
                bg.CacheOption = BitmapCacheOption.OnLoad;
                bg.EndInit();
                Background = new ImageBrush(bg) { Stretch = Stretch.UniformToFill };
            }
            else
            {
                Background = currentTheme == CanvasTheme.Light
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(30, 30, 30));
            }

            Show();
            Dispatcher.BeginInvoke(
                new Action(() => ResetInkInputState()),
                System.Windows.Threading.DispatcherPriority.Loaded
            );
            Activate();
            Focus();
            EnableClickThrough(false);
            Whiteboard.IsHitTestVisible = true;
            Cursor = Cursors.Pen;
            ResetInkInputState();
            OverlayMenu.Visibility = Visibility.Visible;
            OverlayMenu.IsHitTestVisible = true;   // menu clickable
            if (isStartupHelpVisible)
            {
                HelpOverlay.Visibility = Visibility.Collapsed;
            }
        }
        private async Task ExitEditModeAsync()
        {
            await HideOverlayAsync();
            isEditMode = false;
            ImageLayer.IsHitTestVisible = false;
            TextLayer.IsHitTestVisible = false;
            SaveStrokes();
            SaveTextLayer();

            // Bake drawings into the user's wallpaper
            RenderToImage();
            await Task.Delay(150);
            SetAsWallpaper();

            // Clear the opaque background before hiding
            Background = Brushes.Transparent;

            EnableClickThrough(true);
            Cursor = Cursors.Arrow;
            OverlayMenu.Visibility = Visibility.Collapsed;
            OverlayMenu.IsHitTestVisible = false;

            // Hide overlay — drawings now live in the wallpaper
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