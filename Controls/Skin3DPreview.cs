using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NEXLauncher.Controls;

/// <summary>
/// 3D 皮肤预览（skinview3d 风格）：WPF 3D 引擎渲染头 / 身体 / 手臂 / 腿。
/// 立体感来自：三盏方向光分开的明暗面 + 高光材质 + 身体各部件独立关节动画 + 地面投影。
/// 支持鼠标拖动旋转（带惯性）、滚轮缩放，空闲时缓慢转身并轻微摆动四肢。
/// </summary>
public sealed class Skin3DPreview : Grid
{
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), -28);
    private readonly AxisAngleRotation3D _pitch = new(new Vector3D(1, 0, 0), 7);
    private readonly ScaleTransform3D _zoom = new(1.0, 1.0, 1.0);
    private readonly Model3DGroup _model = new();
    private readonly List<GeometryModel3D> _parts = [];

    // 各部件独立关节（0 = 立正）
    private readonly AxisAngleRotation3D _armLRot = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _armRRot = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _legLRot = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _legRRot = new(new Vector3D(1, 0, 0), 0);
    private readonly AxisAngleRotation3D _headRot = new(new Vector3D(1, 0, 0), 0);

    private readonly Viewport3D _viewport = new();
    private readonly DispatcherTimer _timer;
    private readonly Ellipse _shadow;
    private Point _lastPos;
    private bool _dragging;
    private double _velYaw, _velPitch;
    private double _phase;

    public Skin3DPreview()
    {
        ClipToBounds = true;

        // 地面投影（2D 叠加，比 3D 半透明面更稳，不会与贴图挣 Z 序）
        _shadow = new Ellipse
        {
            Width = 118,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 26),
            Opacity = 0.55,
            IsHitTestVisible = false
        };
        _shadow.Fill = new RadialGradientBrush(Color.FromArgb(0xB0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
        Children.Add(_shadow);

        Children.Add(_viewport);
        // 透明矩形铺满控件，保证鼠标命中（Viewport3D 自身无背景、空区域不响应鼠标）
        Children.Add(new Rectangle { Fill = Brushes.Transparent });

        _viewport.Camera = new PerspectiveCamera
        {
            // 更近的相机 + 更大的视场角：近大远小的透视更明显，盒子更有立体感
            Position = new Point3D(0, 1.06, 3.55),
            LookDirection = new Vector3D(0, -0.055, -1),
            FieldOfView = 40
        };

        // 三灯布光：压低环境光制造明暗反差，主光从侧面斜射拉出体积感，
        // 补光提起暗部不死黑，背后轮廓光勾边分离背景
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x36, 0x3C, 0x4A)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xEA, 0xEF, 0xF8), new Vector3D(-0.62, -0.48, -0.72)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x2E, 0x34, 0x42), new Vector3D(0.78, -0.18, -0.55)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x30, 0x3A, 0x4E), new Vector3D(0.12, 0.5, 0.92)));

        // 整体变换：先归一化（像素 → 世界单位），再绕模型中心旋转/缩放
        var group = new Transform3DGroup();
        group.Children.Add(new TranslateTransform3D(0, 1.0, 0)); // 最后移回视口中心
        group.Children.Add(new RotateTransform3D(_yaw));
        group.Children.Add(new RotateTransform3D(_pitch));
        group.Children.Add(_zoom);
        group.Children.Add(new TranslateTransform3D(0, -1.0, 0)); // 模型中心移到原点
        group.Children.Add(new ScaleTransform3D(1.0 / 16, 1.0 / 16, 1.0 / 16)); // 32px 高 → 2 单位
        _model.Transform = group;

        var world = new Model3DGroup();
        world.Children.Add(lights);
        world.Children.Add(_model);
        _viewport.Children.Add(new ModelVisual3D { Content = world });

        MouseLeftButtonDown += (_, e) =>
        {
            _dragging = true;
            _velYaw = _velPitch = 0;
            _lastPos = e.GetPosition(this);
            CaptureMouse();
        };
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); };
        MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);
            var dx = (p.X - _lastPos.X) * 0.55;
            var dy = (p.Y - _lastPos.Y) * 0.40;
            _yaw.Angle = (_yaw.Angle + dx) % 360;
            _pitch.Angle = Math.Clamp(_pitch.Angle + dy, -70, 70);
            _velYaw = dx;
            _velPitch = dy;
            _lastPos = p;
        };
        MouseWheel += (_, e) =>
        {
            var s = Math.Clamp(_zoom.ScaleX + e.Delta * 0.0004, 0.6, 2.4);
            _zoom.ScaleX = _zoom.ScaleY = _zoom.ScaleZ = s;
        };

        // 空闲时缓慢转身 + 四肢轻微摆动（有惯性时先衰减惯性）
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            if (!_dragging)
            {
                if (Math.Abs(_velYaw) > 0.02 || Math.Abs(_velPitch) > 0.02)
                {
                    _yaw.Angle = (_yaw.Angle + _velYaw * 0.6) % 360;
                    _pitch.Angle = Math.Clamp(_pitch.Angle + _velPitch * 0.5, -70, 70);
                    _velYaw *= 0.88;
                    _velPitch *= 0.88;
                }
                else
                {
                    _yaw.Angle = (_yaw.Angle + 0.45) % 360;
                }
            }

            // 呼吸式微动作：手臂前后摆 3°，腿反向 1.6°，头部轻微点头
            _phase += 0.045;
            var s = Math.Sin(_phase);
            _armLRot.Angle = s * 3.2;
            _armRRot.Angle = -s * 3.2;
            _legLRot.Angle = -s * 1.6;
            _legRRot.Angle = s * 1.6;
            _headRot.Angle = Math.Sin(_phase * 0.6) * 1.2;
        };
        _timer.Start();

        // 注意：不要在 Loaded 里加载默认皮肤——账号页会异步加载真实皮肤，
        // Loaded 触发时机可能晚于真实皮肤加载，这里再覆盖会把自定义皮肤冲成默认 Steve。
        // 默认外观由调用方（LoadAccountSkinAsync）负责兜底。
    }

    /// <summary>加载皮肤 PNG（64x64 或旧版 64x32）；传 null 或加载失败时使用默认配色皮肤。</summary>
    public void LoadSkin(string? skinPath)
    {
        BitmapSource? bmp = null;
        try
        {
            if (!string.IsNullOrEmpty(skinPath) && File.Exists(skinPath))
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(skinPath, UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                bmp = img;
            }
        }
        catch { bmp = null; }

        bmp ??= CreateDefaultSkin();
        ApplySkin(bmp);
    }

    /// <summary>直接用已下载的皮肤图像更新预览。</summary>
    public void LoadSkinFromBitmap(BitmapSource bmp)
    {
        bmp.Freeze();
        ApplySkin(bmp);
    }

    private void ApplySkin(BitmapSource bmp)
    {
        // WPF 3D 对贴图固定使用双线性过滤，64x64 皮肤直接贴会发糊。
        // 先用最近邻整数倍放大到 512 宽再当贴图：过滤后像素边缘依然锐利（UV 是归一化坐标，无需改动）。
        var texture = ScaleNearest(bmp, 512);
        var brush = new ImageBrush(texture) { ViewportUnits = BrushMappingMode.Absolute };
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(brush));
        // 高光让布料/金属面更有体积感（数值克制，避免像塑料）
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), 18));

        var legacy = bmp.PixelHeight == 32; // 旧版 64x32：左右共用贴图
        _model.Children.Clear();
        _parts.Clear();

        var body = new Model3DGroup();
        var head = new Model3DGroup { Transform = new RotateTransform3D(_headRot, new Point3D(0, 24, 0)) };
        var armL = new Model3DGroup { Transform = new RotateTransform3D(_armLRot, new Point3D(-6, 22, 0)) };
        var armR = new Model3DGroup { Transform = new RotateTransform3D(_armRRot, new Point3D(6, 22, 0)) };
        var legL = new Model3DGroup { Transform = new RotateTransform3D(_legLRot, new Point3D(-2, 12, 0)) };
        var legR = new Model3DGroup { Transform = new RotateTransform3D(_legRRot, new Point3D(2, 12, 0)) };

        //            宽 高 厚   中心x 中心y                     前面      背面      右面      左面      顶面      底面
        head.Children.Add(Box(8, 8, 8, 0, 28, uv(8, 8, 8, 8), uv(24, 8, 8, 8), uv(0, 8, 8, 8), uv(16, 8, 8, 8), uv(8, 0, 8, 8), uv(16, 0, 8, 8), material));
        body.Children.Add(Box(8, 12, 4, 0, 18, uv(20, 20, 8, 12), uv(32, 20, 8, 12), uv(16, 20, 4, 12), uv(28, 20, 4, 12), uv(20, 16, 8, 4), uv(28, 16, 8, 4), material));

        var armFront = uv(44, 20, 4, 12);
        var armBack = uv(52, 20, 4, 12);
        var armRight = uv(40, 20, 4, 12);
        var armLeft = legacy ? armRight : uv(32, 52, 4, 12);
        var armFrontL = legacy ? armFront : uv(36, 52, 4, 12);
        var armBackL = legacy ? armBack : uv(44, 52, 4, 12);
        var armRightL = legacy ? armRight : uv(40, 52, 4, 12);
        var armTopL = legacy ? uv(44, 16, 4, 4) : uv(44, 48, 4, 4);
        var armBottomL = legacy ? uv(48, 16, 4, 4) : uv(48, 48, 4, 4);

        armR.Children.Add(Box(4, 12, 4, 6, 18, armFront, armBack, armRight, uv(48, 20, 4, 12), uv(44, 16, 4, 4), uv(48, 16, 4, 4), material));
        armL.Children.Add(Box(4, 12, 4, -6, 18, armFrontL, armBackL, armRightL, armLeft, armTopL, armBottomL, material));

        var legFront = uv(4, 20, 4, 12);
        var legBack = uv(12, 20, 4, 12);
        var legRight = uv(0, 20, 4, 12);
        var legLeft = legacy ? legRight : uv(16, 52, 4, 12);
        var legFrontL = legacy ? legFront : uv(20, 52, 4, 12);
        var legBackL = legacy ? legBack : uv(28, 52, 4, 12);
        var legRightL = legacy ? legRight : uv(8, 52, 4, 12);
        var legTopL = legacy ? uv(4, 16, 4, 4) : uv(20, 48, 4, 4);
        var legBottomL = legacy ? uv(8, 16, 4, 4) : uv(24, 48, 4, 4);

        legR.Children.Add(Box(4, 12, 4, 2, 6, legFront, legBack, legRight, uv(8, 20, 4, 12), uv(4, 16, 4, 4), uv(8, 16, 4, 4), material));
        legL.Children.Add(Box(4, 12, 4, -2, 6, legFrontL, legBackL, legRightL, legLeft, legTopL, legBottomL, material));

        _model.Children.Add(body);
        _model.Children.Add(head);
        _model.Children.Add(armL);
        _model.Children.Add(armR);
        _model.Children.Add(legL);
        _model.Children.Add(legR);
    }

    private static Int32Rect uv(int x, int y, int w, int h) => new(x, y, w, h);

    /// <summary>最近邻整数倍放大（像素风贴图专用）：逐像素复制，不做任何插值。
    /// 放大前先把透明像素按 16x16 块用不透明像素的平均色填充——部分皮肤的未使用区域是全透明的，
    /// 直接贴图会渲染成黑洞/看起来缺一块。</summary>
    private static BitmapSource ScaleNearest(BitmapSource src, int targetWidth)
    {
        var f = Math.Max(1, targetWidth / Math.Max(1, src.PixelWidth));

        var sw = src.PixelWidth; var sh = src.PixelHeight;
        var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[sw * sh * 4];
        converted.CopyPixels(pixels, sw * 4, 0);

        // 填充透明区域（Bgra32 字节序：B,G,R,A）
        const int tile = 16;
        // 全局兜底色：整张皮肤的平均色，用于「整块 16x16 区域全透明」的部位
        // （例如只有头部的皮肤：身体/手臂区域全透明，不兜底会渲染成黑洞/黑块）
        long gr = 0, gg = 0, gb = 0; var gCount = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] >= 8) { gr += pixels[i + 2]; gg += pixels[i + 1]; gb += pixels[i]; gCount++; }
        }
        byte fr = gCount > 0 ? (byte)(gr / gCount) : (byte)0x8A;
        byte fg = gCount > 0 ? (byte)(gg / gCount) : (byte)0x8A;
        byte fb = gCount > 0 ? (byte)(gb / gCount) : (byte)0x8A;
        for (var ty = 0; ty < sh; ty += tile)
        {
            for (var tx = 0; tx < sw; tx += tile)
            {
                long sr = 0, sg = 0, sb = 0; var opaque = 0;
                for (var y = ty; y < Math.Min(ty + tile, sh); y++)
                    for (var x = tx; x < Math.Min(tx + tile, sw); x++)
                    {
                        var i = (y * sw + x) * 4;
                        if (pixels[i + 3] >= 8) { sr += pixels[i + 2]; sg += pixels[i + 1]; sb += pixels[i]; opaque++; }
                    }
                // 该块无任何不透明像素时用全局平均色兜底，而不是留透明
                byte r = opaque > 0 ? (byte)(sr / opaque) : fr;
                byte g = opaque > 0 ? (byte)(sg / opaque) : fg;
                byte b = opaque > 0 ? (byte)(sb / opaque) : fb;
                for (var y = ty; y < Math.Min(ty + tile, sh); y++)
                    for (var x = tx; x < Math.Min(tx + tile, sw); x++)
                    {
                        var i = (y * sw + x) * 4;
                        if (pixels[i + 3] < 8)
                        {
                            pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 0xFF;
                        }
                        else if (pixels[i + 3] < 0xFF)
                        {
                            pixels[i + 3] = 0xFF; // 半透明边缘强制不透明，避免 3D 面闪透
                        }
                    }
            }
        }

        if (f <= 1)
        {
            var wb0 = new WriteableBitmap(sw, sh, 96, 96, PixelFormats.Bgra32, null);
            wb0.WritePixels(new Int32Rect(0, 0, sw, sh), pixels, sw * 4, 0);
            wb0.Freeze();
            return wb0;
        }

        var dw = sw * f; var dh = sh * f;
        var scaled = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var sy = (y / f) * sw * 4;
            var drow = y * dw * 4;
            for (var x = 0; x < dw; x++)
            {
                var si = sy + (x / f) * 4;
                var di = drow + x * 4;
                scaled[di] = pixels[si];
                scaled[di + 1] = pixels[si + 1];
                scaled[di + 2] = pixels[si + 2];
                scaled[di + 3] = pixels[si + 3];
            }
        }

        var wb = new WriteableBitmap(dw, dh, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, dw, dh), scaled, dw * 4, 0);
        wb.Freeze();
        return wb;
    }

    /// <summary>添加一个贴图盒子：六个面各自映射到皮肤对应区域。</summary>
    private GeometryModel3D Box(double w, double h, double d, double cx, double cy,
        Int32Rect front, Int32Rect back, Int32Rect right, Int32Rect left, Int32Rect top, Int32Rect bottom,
        Material material)
    {
        var positions = new Point3DCollection();
        var uvs = new PointCollection();
        var indices = new Int32Collection();
        var texW = 64.0;

        void AddFace(Point3D tl, Point3D tr, Point3D br, Point3D bl, Int32Rect r)
        {
            var start = positions.Count;
            positions.Add(tl); positions.Add(tr); positions.Add(br); positions.Add(bl);
            uvs.Add(new Point(r.X / texW, r.Y / 64.0));
            uvs.Add(new Point((r.X + r.Width) / texW, r.Y / 64.0));
            uvs.Add(new Point((r.X + r.Width) / texW, (r.Y + r.Height) / 64.0));
            uvs.Add(new Point(r.X / texW, (r.Y + r.Height) / 64.0));
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }

        var x1 = cx - w / 2; var x2 = cx + w / 2;
        var y1 = cy - h / 2; var y2 = cy + h / 2;
        var z1 = -d / 2; var z2 = d / 2;

        AddFace(new Point3D(x1, y2, z2), new Point3D(x2, y2, z2), new Point3D(x2, y1, z2), new Point3D(x1, y1, z2), front);  // 前
        AddFace(new Point3D(x2, y2, z1), new Point3D(x1, y2, z1), new Point3D(x1, y1, z1), new Point3D(x2, y1, z1), back);   // 后
        AddFace(new Point3D(x2, y2, z2), new Point3D(x2, y2, z1), new Point3D(x2, y1, z1), new Point3D(x2, y1, z2), right);  // 右
        AddFace(new Point3D(x1, y2, z1), new Point3D(x1, y2, z2), new Point3D(x1, y1, z2), new Point3D(x1, y1, z1), left);   // 左
        AddFace(new Point3D(x1, y2, z1), new Point3D(x2, y2, z1), new Point3D(x2, y2, z2), new Point3D(x1, y2, z2), top);    // 顶
        AddFace(new Point3D(x1, y1, z2), new Point3D(x2, y1, z2), new Point3D(x2, y1, z1), new Point3D(x1, y1, z1), bottom); // 底

        var mesh = new MeshGeometry3D
        {
            Positions = positions,
            TextureCoordinates = uvs,
            TriangleIndices = indices
        };
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        _parts.Add(model);
        return model;
    }

    /// <summary>没有皮肤文件时的默认外观（Steve 配色）。</summary>
    private static BitmapSource CreateDefaultSkin()
    {
        var bmp = new WriteableBitmap(64, 64, 96, 96, PixelFormats.Bgra32, null);
        var stride = 64 * 4;
        var pixels = new byte[64 * 64 * 4];

        void Fill(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            for (var yy = y; yy < y + h; yy++)
            {
                for (var xx = x; xx < x + w; xx++)
                {
                    var i = (yy * 64 + xx) * 4;
                    pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 0xFF;
                }
            }
        }

        // 头部：肤色；脸的正面稍亮
        for (var yy = 0; yy < 16; yy++)
            Fill(0, yy, 32, 1, 0xC8, 0x9B, 0x6E);
        Fill(8, 8, 8, 8, 0xD5, 0xA8, 0x7A);          // 脸
        Fill(9, 10, 2, 2, 0x3B, 0x2B, 0x1E);         // 左眼
        Fill(13, 10, 2, 2, 0x3B, 0x2B, 0x1E);        // 右眼
        Fill(11, 13, 3, 1, 0x8A, 0x5A, 0x44);        // 嘴

        // 身体：青色上衣
        Fill(16, 16, 24, 16, 0x00, 0x8A, 0x8A);
        // 手臂：上衣袖子 + 肤色手
        Fill(40, 16, 16, 16, 0x00, 0x8A, 0x8A);
        Fill(44, 28, 8, 4, 0xC8, 0x9B, 0x6E);
        // 腿：深蓝裤子
        Fill(0, 16, 16, 16, 0x3C, 0x44, 0xAA);

        // 64x64 左臂/左腿区域也填色（避免空白）
        Fill(32, 48, 16, 16, 0x00, 0x8A, 0x8A);
        Fill(36, 60, 8, 4, 0xC8, 0x9B, 0x6E);
        Fill(16, 48, 16, 16, 0x3C, 0x44, 0xAA);

        bmp.WritePixels(new Int32Rect(0, 0, 64, 64), pixels, stride, 0);
        bmp.Freeze();
        return bmp;
    }
}
