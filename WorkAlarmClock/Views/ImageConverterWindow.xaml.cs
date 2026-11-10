using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace WorkAlarmClock.Views;

public partial class ImageConverterWindow : Window
{
    private readonly List<string> _files = new();
    private bool _converting;

    public ImageConverterWindow()
    {
        InitializeComponent();
        OutputFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Converted");
        UpdateFiles();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要转换的图片", Multiselect = true, Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths.Where(File.Exists).Where(IsSupported))
            if (!_files.Contains(path, StringComparer.OrdinalIgnoreCase)) _files.Add(path);
        UpdateFiles();
    }

    private static bool IsSupported(string path) => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private void UpdateFiles()
    {
        var items = new List<ImageEntry>();
        foreach (string path in _files.ToArray())
        {
            try { var image = LoadImage(path); items.Add(new ImageEntry(path, Path.GetFileName(path), $"{image.PixelWidth} × {image.PixelHeight} px", image)); }
            catch (Exception ex) { _files.Remove(path); StatusText.Text = "无法读取图片：" + ex.Message; }
        }
        FilesList.ItemsSource = items;
        FilesList.SelectedIndex = items.Count > 0 ? 0 : -1;
        QueueCount.Text = $"{items.Count} 张";
        RefreshPreview();
    }
    private sealed record ImageEntry(string Path, string Name, string Dimensions, BitmapSource Thumbnail);
    private sealed record PreviewEntry(BitmapSource Image, string Label);
    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void Options_Changed(object sender, RoutedEventArgs e) => RefreshPreview();
    private void RefreshPreview()
    {
        if (OutputPaths is null) return;
        var item = FilesList.SelectedItem as ImageEntry;
        SourcePreview.Source = item?.Thumbnail;
        SourcePath.Text = item?.Path ?? "尚未选择图片";
        try
        {
            int[] sizes = SelectedFormat == "ico" ? ParseSizes(SizesInput.Text) : Array.Empty<int>();
            bool separate = SelectedFormat == "ico" && SeparateFiles.IsChecked == true;
            int count = _files.Count * (separate ? sizes.Length : 1);
            OutputSummary.Text = $"预计导出 {count} 个文件" + (SelectedFormat == "ico" && !separate ? $" · 每个 ICO 包含 {sizes.Length} 个尺寸" : "");
            SizePreviews.ItemsSource = item is null ? null : SelectedFormat == "ico"
                ? sizes.Select(size => new PreviewEntry(ResizeSquare(item.Thumbnail, size), $"{size} px")).ToArray()
                : new[] { new PreviewEntry(item.Thumbnail, SelectedFormat.ToUpperInvariant()) };
            OutputPaths.Text = item is null ? "添加图片后显示输出路径" : string.Join(Environment.NewLine,
                (separate ? sizes.Select(size => $"{Path.GetFileNameWithoutExtension(item.Path)}_{size}x{size}.ico") : new[] { Path.GetFileNameWithoutExtension(item.Path) + "." + SelectedFormat })
                .Select(name => UniquePath(Path.Combine(OutputFolder.Text, name))));
        }
        catch (Exception ex) { OutputSummary.Text = ex.Message; SizePreviews.ItemsSource = null; OutputPaths.Clear(); }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { if (!Directory.Exists(OutputFolder.Text)) { StatusText.Text = "文件夹尚未创建，请先转换或选择已有文件夹。"; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { Path.GetFullPath(OutputFolder.Text) }, UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { int index = FilesList.SelectedIndex; if (index >= 0) { _files.RemoveAt(index); UpdateFiles(); } }
    private void Clear_Click(object sender, RoutedEventArgs e) { _files.Clear(); UpdateFiles(); }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择转换后图片的保存位置", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) OutputFolder.Text = dialog.SelectedPath;
    }
    private void FormatPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IcoOptions is not null) IcoOptions.Visibility = SelectedFormat == "ico" ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
    }
    private string SelectedFormat => (FormatPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ico";
    private void Window_PreviewDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); }
    private void FilesList_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_converting) return;
        if (_files.Count == 0) { StatusText.Text = "请先添加图片。"; return; }
        if (string.IsNullOrWhiteSpace(OutputFolder.Text)) { StatusText.Text = "请选择输出文件夹。"; return; }
        int[] sizes;
        try { sizes = SelectedFormat == "ico" ? ParseSizes(SizesInput.Text) : Array.Empty<int>(); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        string[] files = _files.ToArray();
        string output = OutputFolder.Text;
        string format = SelectedFormat;
        bool separate = SeparateFiles.IsChecked == true;
        _converting = true;
        ConvertButton.IsEnabled = false;
        StatusText.Text = "正在转换…";
        try
        {
            int count = await RunStaAsync(() =>
            {
                Directory.CreateDirectory(output);
                int converted = 0;
                foreach (string file in files)
                {
                    BitmapSource source = LoadImage(file);
                    string destination = Path.Combine(output, Path.GetFileNameWithoutExtension(file) + (format == "ico" ? ".ico" : "." + format));
                    destination = UniquePath(destination);
                    if (format == "ico" && separate)
                    {
                        foreach (int size in sizes)
                        {
                            string sizedPath = UniquePath(Path.Combine(output, $"{Path.GetFileNameWithoutExtension(file)}_{size}x{size}.ico"));
                            WriteIco(source, new[] { size }, sizedPath); converted++;
                        }
                        continue;
                    }
                    if (format == "ico") WriteIco(source, sizes, destination);
                    else SaveRaster(source, destination, format);
                    converted++;
                }
                return converted;
            });
            StatusText.Text = $"转换完成：{count} 个文件已保存到 {output}";
        }
        catch (Exception ex) { StatusText.Text = "转换失败：" + ex.Message; }
        finally { _converting = false; ConvertButton.IsEnabled = true; }
    }

    private static Task<int> RunStaAsync(Func<int> action)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() => { try { completion.SetResult(action()); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static int[] ParseSizes(string value)
    {
        int[] sizes = value.Split(new[] { ',', '，', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => int.TryParse(item, out int size) ? size : throw new FormatException($"无效尺寸：{item}"))
            .Distinct().OrderBy(size => size).ToArray();
        if (sizes.Length == 0 || sizes.Any(size => size < 1 || size > 256)) throw new FormatException("ICO 尺寸须为 1–256 px，例如：16,32,48,256。");
        return sizes;
    }

    private static BitmapSource LoadImage(string path)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    private static BitmapSource ResizeSquare(BitmapSource source, int size)
    {
        double scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
        int width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));
            drawing.DrawImage(source, new Rect((size - width) / 2d, (size - height) / 2d, width, height));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] PngBytes(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteIco(BitmapSource source, int[] sizes, string path)
    {
        byte[][] images = sizes.Select(size => PngBytes(ResizeSquare(source, size))).ToArray();
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
        }
        foreach (byte[] image in images) writer.Write(image);
    }

    private static void SaveRaster(BitmapSource source, string path, string format)
    {
        BitmapSource output = source;
        BitmapEncoder encoder;
        if (format == "jpg")
        {
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
                drawing.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            }
            var flattened = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            flattened.Render(visual); flattened.Freeze();
            output = new FormatConvertedBitmap(flattened, PixelFormats.Bgr24, null, 0); output.Freeze();
            encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        }
        else encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        int index = 2;
        string candidate;
        do { candidate = Path.Combine(directory, $"{name} ({index++}){extension}"); } while (File.Exists(candidate));
        return candidate;
    }
}
