using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using SD = System.Drawing;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using WorkAlarmClock.Infrastructure;

namespace WorkAlarmClock.Views;

public partial class CodeToolWindow : Window
{
    private BitmapSource? _generated;

    public CodeToolWindow()
    {
        InitializeComponent();
        LoadPreferences();
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string content = InputText.Text.Trim();
            if (content.Length == 0) throw new System.FormatException("请输入要生成的内容。");
            int width = ToPixels(WidthInput.Text, "宽度");
            int height = ToPixels(HeightInput.Text, "高度");
            string formatName = ((ComboBoxItem)FormatPicker.SelectedItem).Tag.ToString()!;
            BarcodeFormat format = Enum.Parse<BarcodeFormat>(formatName);
            if (format == BarcodeFormat.QR_CODE && width != height)
                throw new System.FormatException("二维码需要正方形尺寸，请将宽度和高度设为相同值。");
            var hints = new System.Collections.Generic.Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = format == BarcodeFormat.QR_CODE ? 2 : 1
            };
            if (format == BarcodeFormat.QR_CODE) hints[EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M;
            BitMatrix matrix = new MultiFormatWriter().encode(content, format, width, height, hints);
            int actualWidth = matrix.Width;
            int actualHeight = matrix.Height;
            byte[] pixels = new byte[actualWidth * actualHeight];
            for (int y = 0; y < actualHeight; y++)
                for (int x = 0; x < actualWidth; x++) pixels[y * actualWidth + x] = matrix[x, y] ? (byte)0 : (byte)255;
            _generated = BitmapSource.Create(actualWidth, actualHeight, 96, 96, PixelFormats.Gray8, null, pixels, actualWidth);
            _generated.Freeze();
            CodePreview.Source = _generated;
            var fade = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220));
            CodePreview.BeginAnimation(OpacityProperty, fade);
            StatusText.Text = $"已更新预览：{actualWidth} × {actualHeight} px";
            try { SavePreferences(); }
            catch (Exception ex) { StatusText.Text += $"；尺寸保存失败：{ex.Message}"; }
        }
        catch (Exception ex) { StatusText.Text = "生成失败：" + ex.Message; }
    }

    private int ToPixels(string raw, string label)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
            throw new System.FormatException($"{label}必须是大于 0 的数字。");
        string unit = ((ComboBoxItem)UnitPicker.SelectedItem).Content.ToString()!;
        double px = unit switch { "mm" => value * 96 / 25.4, "cm" => value * 96 / 2.54, _ => value };
        int result = (int)Math.Round(px);
        if (result is < 64 or > 4096) throw new System.FormatException($"{label}换算后需在 64–4096 px 之间。");
        return result;
    }

    private void PasteText_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText()) { InputText.Text = Clipboard.GetText(); InputText.Focus(); StatusText.Text = "已粘贴剪贴板文本。"; }
            else StatusText.Text = "剪贴板中没有文本。";
        }
        catch (Exception ex) { StatusText.Text = "读取剪贴板失败：" + ex.Message; }
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择二维码或条码图片", Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try { using var stream = File.OpenRead(dialog.FileName); DecodeImage(BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)); }
        catch (Exception ex) { StatusText.Text = "打开图片失败：" + ex.Message; }
    }

    private void DecodeClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BitmapSource? image = GetClipboardImage();
            if (image is not null) DecodeImage(image);
            else StatusText.Text = "剪贴板中没有可读取的图片；请先复制图片，再点击此按钮。";
        }
        catch (Exception ex) { StatusText.Text = "读取剪贴板失败：" + ex.Message; }
    }

    private static BitmapSource? GetClipboardImage()
    {
        IDataObject? data = Clipboard.GetDataObject();
        if (data is null) return null;

        foreach (string format in new[] { "PNG", "image/png", "JFIF", "image/jpeg", "image/bmp", DataFormats.Bitmap, DataFormats.Dib })
        {
            try
            {
                if (!data.GetDataPresent(format, true)) continue;
                object? value = data.GetData(format, true);
                if (value is BitmapSource source) return source;
                if (value is byte[] bytes) return DecodeImageBytes(bytes);
                if (value is MemoryStream memory) return DecodeImageBytes(memory.ToArray());
                if (value is Stream stream)
                {
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    return DecodeImageBytes(copy.ToArray());
                }
                if (value is SD.Bitmap bitmap)
                {
                    using var encoded = new MemoryStream();
                    bitmap.Save(encoded, SD.Imaging.ImageFormat.Png);
                    return DecodeImageBytes(encoded.ToArray());
                }
            }
            catch { }
        }

        if (data.GetDataPresent(DataFormats.FileDrop, true) && data.GetData(DataFormats.FileDrop, true) is string[] files)
        {
            foreach (string file in files)
            {
                if (!File.Exists(file)) continue;
                try { using var stream = File.OpenRead(file); return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); }
                catch { }
            }
        }
        try { return Clipboard.ContainsImage() ? Clipboard.GetImage() : null; }
        catch { return null; }
    }

    private static BitmapSource DecodeImageBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            Generate_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void DecodeImage(BitmapSource? source)
    {
        if (source is null) { StatusText.Text = "没有可识别的图片。"; return; }
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] bgra = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(bgra, stride, 0);
        var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, TryInverted = true } };
        Result? result = reader.Decode(new BgraLuminanceSource(bgra, converted.PixelWidth, converted.PixelHeight));
        if (result is null) { StatusText.Text = "未识别到二维码或条码，请尝试更清晰、完整的图片。"; return; }
        DecodedText.Text = result.Text;
        StatusText.Text = $"识别成功：{result.BarcodeFormat}";
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (_generated is null) { StatusText.Text = "请先生成图片。"; return; }
        Clipboard.SetImage(_generated);
        StatusText.Text = "图片已复制到剪贴板。";
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DecodedText.Text)) { StatusText.Text = "没有可复制的识别结果。"; return; }
        Clipboard.SetText(DecodedText.Text);
        StatusText.Text = "识别结果已复制到剪贴板。";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_generated is null) { StatusText.Text = "请先生成图片。"; return; }
        var dialog = new SaveFileDialog { Title = "导出条码图片", Filter = "PNG 图片|*.png", FileName = "二维码.png", DefaultExt = ".png" };
        if (dialog.ShowDialog(this) != true) return;
        using var stream = File.Create(dialog.FileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_generated));
        encoder.Save(stream);
        StatusText.Text = "PNG 已导出。";
    }

    private void SaveSize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = ToPixels(WidthInput.Text, "宽度");
            _ = ToPixels(HeightInput.Text, "高度");
            SavePreferences();
            StatusText.Text = "尺寸与单位已保存到 SQLite。";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void LoadPreferences()
    {
        try
        {
            using var db = new SqliteConnection("Data Source=" + AppEnvironment.DbPath);
            db.Open();
            using (var create = db.CreateCommand())
            {
                create.CommandText = "CREATE TABLE IF NOT EXISTS utility_preferences(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
                create.ExecuteNonQuery();
            }
            using var command = db.CreateCommand();
            command.CommandText = "SELECT value FROM utility_preferences WHERE key='code_tool_size';";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return;
            string[] values = reader.GetString(0).Split('|');
            if (values.Length == 3) { WidthInput.Text = values[0]; HeightInput.Text = values[1]; UnitPicker.SelectedIndex = int.TryParse(values[2], out int unit) ? Math.Clamp(unit, 0, 2) : 0; }
        }
        catch (Exception ex) { StatusText.Text = "读取尺寸设置失败：" + ex.Message; }
    }

    private void SavePreferences()
    {
        using var db = new SqliteConnection("Data Source=" + AppEnvironment.DbPath);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS utility_preferences(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO utility_preferences(key,value) VALUES('code_tool_size',$value) ON CONFLICT(key) DO UPDATE SET value=$value;";
        command.Parameters.AddWithValue("$value", $"{WidthInput.Text}|{HeightInput.Text}|{UnitPicker.SelectedIndex}");
        command.ExecuteNonQuery();
    }

    private sealed class BgraLuminanceSource : LuminanceSource
    {
        private readonly byte[] _luminance;
        public BgraLuminanceSource(byte[] bgra, int width, int height) : base(width, height)
        {
            _luminance = new byte[width * height];
            for (int i = 0, p = 0; i < _luminance.Length; i++, p += 4)
                _luminance[i] = (byte)((bgra[p + 2] * 306 + bgra[p + 1] * 601 + bgra[p] * 117) >> 10);
        }
        public override byte[] Matrix => _luminance;
        public override byte[] getRow(int y, byte[] row)
        {
            if (row is null || row.Length < Width) row = new byte[Width];
            Array.Copy(_luminance, y * Width, row, 0, Width);
            return row;
        }
    }
}
