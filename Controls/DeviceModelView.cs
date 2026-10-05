using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using alpha_island.Services;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Size = System.Windows.Size;

namespace alpha_island.Controls
{
    /// <summary>
    /// 蓝牙设备 3D 旋转模型视图（类似 iOS AirPods 配对动画）。
    ///
    /// 性能约定：
    ///  * 使用 <see cref="CompositionTarget.Rendering"/> 驱动旋转，仅在 <see cref="IsRotating"/> = true 时帧循环；
    ///  * 几何体为程序化生成的椭球体网格，面数适中，适合 32×32 ~ 64×64 小尺寸显示；
    ///  * 作为 <see cref="FrameworkElement"/> 的单子视觉元素承载 <see cref="Viewport3D"/>。
    /// </summary>
    public sealed class DeviceModelView : FrameworkElement
    {
        // ────────────── 依赖属性 ──────────────

        /// <summary>是否正在旋转。</summary>
        public static readonly DependencyProperty IsRotatingProperty =
            DependencyProperty.Register(nameof(IsRotating), typeof(bool), typeof(DeviceModelView),
                new FrameworkPropertyMetadata(false, OnIsRotatingChanged));

        /// <summary>设备类别（预留：未来切换不同几何形状）。</summary>
        public static readonly DependencyProperty DeviceCategoryProperty =
            DependencyProperty.Register(nameof(DeviceCategory), typeof(BluetoothDeviceCategory), typeof(DeviceModelView),
                new FrameworkPropertyMetadata(BluetoothDeviceCategory.Audio, OnDeviceCategoryChanged));

        /// <summary>是否正在旋转。</summary>
        public bool IsRotating
        {
            get => (bool)GetValue(IsRotatingProperty);
            set => SetValue(IsRotatingProperty, value);
        }

        /// <summary>设备类别（预留：未来切换不同几何形状）。</summary>
        public BluetoothDeviceCategory DeviceCategory
        {
            get => (BluetoothDeviceCategory)GetValue(DeviceCategoryProperty);
            set => SetValue(DeviceCategoryProperty, value);
        }

        // ────────────── 内部 3D 场景 ──────────────

        private readonly Viewport3D _viewport;
        private readonly AxisAngleRotation3D _rotation;
        private readonly GeometryModel3D _model;

        // ────────────── 帧循环状态 ──────────────

        private bool _rendering;
        private double _lastTick = -1;

        /// <summary>旋转速度：度/秒。</summary>
        private const double RotationSpeed = 30.0;

        // ────────────── 椭球体参数 ──────────────

        /// <summary>椭球体半径 (X, Y, Z)：近似无线耳塞外形。</summary>
        private const double RadiusX = 0.35;
        private const double RadiusY = 0.50;
        private const double RadiusZ = 0.30;

        /// <summary>纬线层数。</summary>
        private const int Stacks = 16;
        /// <summary>经线切片数。</summary>
        private const int Slices = 24;

        // ────────────── 构造函数 ──────────────

        public DeviceModelView()
        {
            // ── 几何体：默认音频设备（如 AirPods 耳塞形态） ──
            var mesh = GenerateEarbud();

            // ── 材质：高光陶瓷白 ──
            var materialGroup = new MaterialGroup();
            materialGroup.Children.Add(new DiffuseMaterial(
                new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF8))));
            materialGroup.Children.Add(new SpecularMaterial(
                new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                specularPower: 95.0));

            // ── 旋转变换 ──
            _rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            var rotateTransform = new RotateTransform3D(_rotation, new Point3D(0, 0, 0));

            // ── 模型 ──
            _model = new GeometryModel3D
            {
                Geometry = mesh,
                Material = materialGroup,
                BackMaterial = materialGroup,
                Transform = rotateTransform,
            };

            // ── 灯光：明亮主光 + 柔和环境光 + 轮廓补光 ──
            var ambientLight = new AmbientLight(Color.FromRgb(0x88, 0x88, 0x99));
            var keyLight = new DirectionalLight(Color.FromRgb(0xFF, 0xFF, 0xFF), new Vector3D(-1, -1, -2));
            var fillLight = new DirectionalLight(Color.FromRgb(0x66, 0x66, 0x77), new Vector3D(1, 0.5, 1));

            // ── 模型组 ──
            var modelGroup = new Model3DGroup();
            modelGroup.Children.Add(ambientLight);
            modelGroup.Children.Add(keyLight);
            modelGroup.Children.Add(fillLight);
            modelGroup.Children.Add(_model);

            // ── 相机：正对模型、略有透视 ──
            var camera = new PerspectiveCamera
            {
                Position = new Point3D(0, 0.05, 1.8),
                LookDirection = new Vector3D(0, -0.05, -1),
                UpDirection = new Vector3D(0, 1, 0),
                FieldOfView = 32,
                NearPlaneDistance = 0.1,
                FarPlaneDistance = 100,
            };

            // ── 构建 Viewport3D ──
            _viewport = new Viewport3D
            {
                Camera = camera,
                ClipToBounds = true,
            };
            _viewport.Children.Add(new ModelVisual3D { Content = modelGroup });

            // ── 作为子视觉元素挂载 ──
            AddVisualChild(_viewport);
            AddLogicalChild(_viewport);

            // ── 生命周期 ──
            Loaded += (_, _) =>
            {
                alpha_island.Animation.MotionPolicy.Changed += SyncMotion;
                SyncMotion();
            };
            Unloaded += (_, _) =>
            {
                alpha_island.Animation.MotionPolicy.Changed -= SyncMotion;
                StopRendering();
            };
            IsVisibleChanged += (_, _) => SyncMotion();
        }

        // ────────────── Visual 树 ──────────────

        /// <summary>子视觉元素数量（始终为 1）。</summary>
        protected override int VisualChildrenCount => 1;

        /// <summary>返回唯一的子视觉元素。</summary>
        protected override Visual GetVisualChild(int index)
        {
            if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
            return _viewport;
        }

        // ────────────── 布局 ──────────────

        /// <summary>测量阶段：让 Viewport3D 占据全部可用空间。</summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            // 如果上层给了无穷大，回退到合理默认尺寸
            double w = double.IsInfinity(availableSize.Width) ? 48 : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? 48 : availableSize.Height;
            var desired = new Size(w, h);

            _viewport.Measure(desired);
            return desired;
        }

        /// <summary>排列阶段：将 Viewport3D 排满控件区域。</summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            _viewport.Arrange(new Rect(finalSize));
            return finalSize;
        }

        // ────────────── 依赖属性变更 ──────────────

        private static void OnIsRotatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (DeviceModelView)d;
            if ((bool)e.NewValue)
                view.StartRendering();
            else
                view.StopRendering();
        }

        private static void OnDeviceCategoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (DeviceModelView)d;
            view.RebuildGeometry((BluetoothDeviceCategory)e.NewValue);
        }

        /// <summary>
        /// 根据设备类别重建网格几何体。
        /// </summary>
        private void RebuildGeometry(BluetoothDeviceCategory category)
        {
            var mesh = category switch
            {
                BluetoothDeviceCategory.Audio => GenerateEarbud(),
                BluetoothDeviceCategory.Input => GenerateEllipsoid(0.35, 0.18, 0.50, Stacks, Slices),
                _ => GenerateEllipsoid(RadiusX, RadiusY, RadiusZ, Stacks, Slices),
            };
            _model.Geometry = mesh;
        }

        // ────────────── 帧循环 ──────────────

        private void StartRendering()
        {
            if (_rendering || !IsLoaded || !IsVisible || alpha_island.Animation.MotionPolicy.InstantMorph ||
                alpha_island.Animation.MotionPolicy.PowerSave) return;
            _rendering = true;
            _lastTick = -1;
            CompositionTarget.Rendering += OnRendering;
        }

        private void SyncMotion()
        {
            if (IsRotating && IsVisible && IsLoaded && !alpha_island.Animation.MotionPolicy.InstantMorph &&
                !alpha_island.Animation.MotionPolicy.PowerSave) StartRendering();
            else StopRendering();
        }

        private void StopRendering()
        {
            if (!_rendering) return;
            _rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            double dt = _lastTick < 0 ? 1.0 / 60.0 : now - _lastTick;
            _lastTick = now;

            // 防止帧间隔异常
            if (dt <= 0) dt = 1.0 / 60.0;
            if (dt > 0.12) dt = 0.12;

            // 累积旋转角度（0 ~ 360 循环）
            double angle = _rotation.Angle + RotationSpeed * dt;
            if (angle >= 360.0) angle -= 360.0;
            _rotation.Angle = angle;
        }

        // ────────────── 几何体生成 ──────────────

        /// <summary>
        /// 生成无线耳塞（类似 AirPods）复合网格：包含球形发声部腔体与向下延伸的耳柄。
        /// </summary>
        private static MeshGeometry3D GenerateEarbud()
        {
            var mesh = new MeshGeometry3D();

            // 1. 耳塞主腔体（稍偏一侧的球形头部）
            AppendEllipsoid(mesh, new Point3D(0.04, 0.12, 0), 0.22, 0.20, 0.20, 14, 20);

            // 2. 耳塞柄（垂直向下圆柱）
            AppendCylinder(mesh, new Point3D(0, 0.10, 0), new Point3D(0, -0.30, 0), 0.065, 20);

            // 3. 耳柄底部半球收尾
            AppendEllipsoid(mesh, new Point3D(0, -0.30, 0), 0.065, 0.065, 0.065, 8, 20);

            mesh.Freeze();
            return mesh;
        }

        /// <summary>向网格中追加一个椭球体。</summary>
        private static void AppendEllipsoid(MeshGeometry3D mesh, Point3D center, double rx, double ry, double rz, int stacks, int slices)
        {
            int baseIndex = mesh.Positions.Count;
            for (int stack = 0; stack <= stacks; stack++)
            {
                double phi = Math.PI * stack / stacks;
                double sinPhi = Math.Sin(phi);
                double cosPhi = Math.Cos(phi);

                for (int slice = 0; slice <= slices; slice++)
                {
                    double theta = 2.0 * Math.PI * slice / slices;
                    double sinTheta = Math.Sin(theta);
                    double cosTheta = Math.Cos(theta);

                    double nx = sinPhi * cosTheta;
                    double ny = cosPhi;
                    double nz = sinPhi * sinTheta;

                    mesh.Positions.Add(new Point3D(center.X + rx * nx, center.Y + ry * ny, center.Z + rz * nz));

                    double gnx = nx / rx, gny = ny / ry, gnz = nz / rz;
                    double len = Math.Sqrt(gnx * gnx + gny * gny + gnz * gnz);
                    if (len > 1e-10) { gnx /= len; gny /= len; gnz /= len; }
                    mesh.Normals.Add(new Vector3D(gnx, gny, gnz));
                }
            }

            int stride = slices + 1;
            for (int stack = 0; stack < stacks; stack++)
            {
                for (int slice = 0; slice < slices; slice++)
                {
                    int topLeft = baseIndex + stack * stride + slice;
                    int topRight = topLeft + 1;
                    int bottomLeft = topLeft + stride;
                    int bottomRight = bottomLeft + 1;

                    mesh.TriangleIndices.Add(topLeft);
                    mesh.TriangleIndices.Add(bottomLeft);
                    mesh.TriangleIndices.Add(topRight);

                    mesh.TriangleIndices.Add(topRight);
                    mesh.TriangleIndices.Add(bottomLeft);
                    mesh.TriangleIndices.Add(bottomRight);
                }
            }
        }

        /// <summary>向网格中追加一个连接两点的圆柱面。</summary>
        private static void AppendCylinder(MeshGeometry3D mesh, Point3D p1, Point3D p2, double radius, int slices)
        {
            int baseIndex = mesh.Positions.Count;
            Vector3D axis = p2 - p1;
            double height = axis.Length;
            if (height < 1e-6) return;
            Vector3D dir = axis / height;

            Vector3D refVec = Math.Abs(dir.Y) < 0.9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0);
            Vector3D u = Vector3D.CrossProduct(dir, refVec);
            u.Normalize();
            Vector3D v = Vector3D.CrossProduct(dir, u);
            v.Normalize();

            for (int ring = 0; ring <= 1; ring++)
            {
                Point3D basePoint = ring == 0 ? p1 : p2;
                for (int slice = 0; slice <= slices; slice++)
                {
                    double theta = 2.0 * Math.PI * slice / slices;
                    double cos = Math.Cos(theta);
                    double sin = Math.Sin(theta);
                    Vector3D normal = u * cos + v * sin;
                    Point3D pos = basePoint + normal * radius;

                    mesh.Positions.Add(pos);
                    mesh.Normals.Add(normal);
                }
            }

            int stride = slices + 1;
            for (int slice = 0; slice < slices; slice++)
            {
                int topLeft = baseIndex + slice;
                int topRight = topLeft + 1;
                int bottomLeft = baseIndex + stride + slice;
                int bottomRight = bottomLeft + 1;

                mesh.TriangleIndices.Add(topLeft);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(topRight);

                mesh.TriangleIndices.Add(topRight);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(bottomRight);
            }
        }

        /// <summary>
        /// 生成独立椭球体网格。
        /// </summary>
        private static MeshGeometry3D GenerateEllipsoid(
            double rx, double ry, double rz, int stacks, int slices)
        {
            var mesh = new MeshGeometry3D();
            AppendEllipsoid(mesh, new Point3D(0, 0, 0), rx, ry, rz, stacks, slices);
            mesh.Freeze();
            return mesh;
        }
    }
}
