using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using System.Globalization;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace BetaflightMagVisualizer
{
    public partial class Form1 : Form
    {
        // UI controls
        private GLControl glControl;
        private Button btnLoadCsv;
        private Button btnLoadCsv2;
        private TextBox txtCliOutput;
        private Label lblStats;
        /// <summary>Numeric controls for the previous X, Y, and Z coordinates.</summary>
        private NumericUpDown numOldX, numOldY, numOldZ;
        private NumericUpDown numTrimStart;
        private NumericUpDown numTrimEnd;

        /// <summary>Checkbox for applying ellipsoid correction.</summary>
        private CheckBox chkApplyEllipsoid;
        /// <summary>Checkbox for applying only the center offset while preserving the ellipsoid.</summary>
        private CheckBox chkCenterOnly;
        /// <summary>Checkbox for showing or hiding the second measured point set.</summary>
        private CheckBox chkShowCsv2;

        // Primary measurement and calibration results
        /// <summary>complete time series from the file</summary>
        private List<Vector3> allCsvPoints = new List<Vector3>();
        /// <summary>The collection of raw vector points.</summary>
        private List<Vector3> rawPoints = new List<Vector3>();
        private Vector3 sphereCenter = Vector3.Zero;
        private float sphereRadius = 0f;
        private Vector3[] ellipsoidAxes = new Vector3[3]; // three principal ellipsoid semi-axes (direction * radius)
        private Vector3 ellipsoidScale = Vector3.One; // ellipsoid compensation (axis-aligned only)
        private Vector3 lastRadii = Vector3.One;
        private Matrix3 softIronMatrix = Matrix3.Identity; // general ellipsoid compensation, including arbitrary rotation relative to the base axes

        // Secondary measurement used for comparison
        private List<Vector3> allCsvPoints2 = new List<Vector3>();
        private List<Vector3> rawPoints2 = new List<Vector3>();

        // Visualization state
        private float zoom = 2.5f;
        // Quaternion for mouse-driven "ArcBall" screen rotation
        private Quaternion sceneRotation = Quaternion.Identity;
        private Vector3 trackballLastVec = Vector3.Zero;
        private bool isDragging = false;
        private int texLabelX, texLabelY, texLabelZ;


        public Form1()
        {
            InitializeCustomUI();
            GenerateSimulatedData(1200); // generate 1,200 test points at startup
        }

        /// <summary>
        /// Initializes the interface for loading and comparing magnetometer data, configuring calibration options,
        /// displaying Betaflight CLI output, and viewing interactive 3D plots.
        /// </summary>
        private void InitializeCustomUI()
        {
            this.Text = "Betaflight Magnetometer 3D Calibrator (OpenTK)";
            this.Size = new Size(1100, 750);
            this.StartPosition = FormStartPosition.CenterScreen;

            Panel sidePanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 320,
                Padding = new Padding(10),
                BackColor = Color.FromArgb(240, 240, 240)
            };
            this.Controls.Add(sidePanel);
            // ----------------------------------------------------------------------------------------
            btnLoadCsv = new Button
            {
                Text = "Load Blackbox CSV...",
                Dock = DockStyle.Top,
                Height = 40,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            btnLoadCsv.Click += BtnLoadCsv_Click;
            sidePanel.Controls.Add(btnLoadCsv);
            // ----------------------------------------------------------------------------------------
            btnLoadCsv2 = new Button
            {
                Text = "Load second CSV (comparison)...",
                Dock = DockStyle.Top,
                Height = 35,
                Font = new Font("Segoe UI", 9.5f)
            };
            btnLoadCsv2.Click += BtnLoadCsv2_Click;
            sidePanel.Controls.Add(btnLoadCsv2);
            btnLoadCsv2.BringToFront(); // call immediately after btnLoadCsv so it appears below it
            // ----------------------------------------------------------------------------------------
            chkShowCsv2 = new CheckBox
            {
                Text = "Show second point set (cyan)",
                Checked = true,
                Enabled = false, // enabled automatically after the second file is loaded
                Dock = DockStyle.Top,
                Height = 28,
                Font = new Font("Segoe UI", 9.0f),
                Padding = new Padding(5, 2, 0, 0)
            };

            chkShowCsv2.CheckedChanged += (s, e) => glControl.Invalidate();

            sidePanel.Controls.Add(chkShowCsv2);
            chkShowCsv2.BringToFront();

            // ----------------------------------------------------------------------------------------

            GroupBox grpOldOffsets = new GroupBox
            {
                Text = "Original CLI offsets (get mag_zero)",
                Dock = DockStyle.Top,
                Height = 130,
                Padding = new Padding(10)
            };
            sidePanel.Controls.Add(grpOldOffsets);
            grpOldOffsets.BringToFront();

            numOldX = CreateOffsetInput(grpOldOffsets, "X:", 25);
            numOldY = CreateOffsetInput(grpOldOffsets, "Y:", 55);
            numOldZ = CreateOffsetInput(grpOldOffsets, "Z:", 85);

            // Recalculate dynamically when the original offsets change
            numOldX.ValueChanged += (s, e) => ApplyTrimAndRecalculate();
            numOldY.ValueChanged += (s, e) => ApplyTrimAndRecalculate();
            numOldZ.ValueChanged += (s, e) => ApplyTrimAndRecalculate();

            chkApplyEllipsoid = new CheckBox
            {
                Text = "Apply ellipsoid correction (Soft-Iron)",
                Checked = true,
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Padding = new Padding(0, 5, 0, 0)
            };
            chkApplyEllipsoid.CheckedChanged += (s, e) =>
            {
                if (chkApplyEllipsoid.Checked) chkCenterOnly.Checked = false;
                UpdateStatsDisplay();
                glControl.Invalidate();
            };


            sidePanel.Controls.Add(chkApplyEllipsoid);
            chkApplyEllipsoid.BringToFront();
            // ----------------------------------------------------------------
            chkCenterOnly = new CheckBox
            {
                Text = "Center only (Hard-Iron / standard BF)",
                Dock = DockStyle.Top,
                Height = 30,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Padding = new Padding(0, 5, 0, 0)
            };

            chkCenterOnly.CheckedChanged += (s, e) =>
            {
                if (chkCenterOnly.Checked) chkApplyEllipsoid.Checked = false;
                UpdateStatsDisplay();
                glControl.Invalidate();
            };

            sidePanel.Controls.Add(chkCenterOnly);
            chkCenterOnly.BringToFront();
            // ----------------------------------------------------------------

            GroupBox grpTrim = new GroupBox
            {
                Text = "Trim start and end of log (%)",
                Dock = DockStyle.Top,
                Height = 75,
                Padding = new Padding(10)
            };
            sidePanel.Controls.Add(grpTrim);
            grpTrim.BringToFront();

            Label lblTrimStart = new Label { Text = "Start %:", Location = new Point(10, 28), AutoSize = true };
            numTrimStart = new NumericUpDown
            {
                Location = new Point(80, 25),
                Width = 60,
                Minimum = 0,
                Maximum = 45,
                Value = 5 // default trim of the first 5% of the recording
            };

            Label lblTrimEnd = new Label { Text = "End %:", Location = new Point(160, 28), AutoSize = true };
            numTrimEnd = new NumericUpDown
            {
                Location = new Point(225, 25),
                Width = 60,
                Minimum = 0,
                Maximum = 45,
                Value = 5 // default trim of the last 5% of the recording
            };

            numTrimStart.ValueChanged += (s, e) => ApplyTrimAndRecalculate();
            numTrimEnd.ValueChanged += (s, e) => ApplyTrimAndRecalculate();

            grpTrim.Controls.Add(lblTrimStart);
            grpTrim.Controls.Add(numTrimStart);
            grpTrim.Controls.Add(lblTrimEnd);
            grpTrim.Controls.Add(numTrimEnd);


            // ----------------------------------------------------------------
            lblStats = new Label
            {
                Text = "Load a CSV file for calculation.\n\n3D controls:\n- Left mouse button: Rotate\n- Mouse wheel: Zoom",
                Dock = DockStyle.Top,
                Height = 210,
                Padding = new Padding(0, 10, 0, 10),
                Font = new Font("Segoe UI", 9.5f)
            };
            sidePanel.Controls.Add(lblStats);
            lblStats.BringToFront();

            Label lblCli = new Label
            {
                Text = "Betaflight CLI commands:",
                Dock = DockStyle.Top,
                Height = 25,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            sidePanel.Controls.Add(lblCli);
            lblCli.BringToFront();

            txtCliOutput = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10f),
                BackColor = Color.White
            };
            sidePanel.Controls.Add(txtCliOutput);
            txtCliOutput.BringToFront();

            var settings = new GLControlSettings
            {
                Profile = ContextProfile.Compatability,
                API = ContextAPI.OpenGL,
                DepthBits = 24
            };

            glControl = new GLControl(settings)
            {
                Dock = DockStyle.Fill
            };

            glControl.Load += GlControl_Load;
            glControl.Resize += GlControl_Resize;
            glControl.Paint += GlControl_Paint;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseWheel += GlControl_MouseWheel;

            this.Controls.Add(glControl);
            glControl.BringToFront();
        }

        /// <summary>
        /// Creates a numeric input control with a label for specifying an offset value.
        /// </summary>
        /// <param name="parent">The parent control to which the input control will be added.</param>
        /// <param name="labelText">The text for the label.</param>
        /// <param name="top">The top position of the control.</param>
        /// <returns>The created NumericUpDown control.</returns>
        private NumericUpDown CreateOffsetInput(Control parent, string labelText, int top)
        {
            Label lbl = new Label { Text = labelText, Left = 15, Top = top + 3, Width = 30 };
            NumericUpDown num = new NumericUpDown
            {
                Left = 50,
                Top = top,
                Width = 120,
                Minimum = -32768,
                Maximum = 32767,
                Value = 0
            };
            parent.Controls.Add(lbl);
            parent.Controls.Add(num);
            return num;
        }

        /// <summary>
        /// Selects a CSV file and processes it.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void BtnLoadCsv_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Blackbox CSV (*.csv)|*.csv|All files (*.*)|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    ProcessCsvFile(ofd.FileName);
                }
            }
        }
        /// <summary>
        /// Loads unique magnetic points from a selected CSV file as the second data set and recalculates the trimmed
        /// results.
        /// </summary>
        /// <param name="sender">The control that raised the event.</param>
        /// <param name="e">The event data.</param>
        private void BtnLoadCsv2_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        allCsvPoints2 = LoadUniqueMagPoints(ofd.FileName, 0f, 0f, 0f);
                        chkShowCsv2.Enabled = true;
                        chkShowCsv2.Checked = true;
                        ApplyTrimAndRecalculate();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading the second file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// Loads unique magnetometer points from a CSV file and recalculates them after applying trimming.
        /// </summary>
        /// <remarks>Displays a warning if the file contains fewer than 20 points. Displays an error
        /// message if loading or recalculation fails.</remarks>
        /// <param name="filePath">The path to the CSV file to process.</param>
        private void ProcessCsvFile(string filePath)
        {
            try
            {
                // Load raw points from the file; original offsets are added during calculation
                allCsvPoints = LoadUniqueMagPoints(filePath, 0f, 0f, 0f);

                if (allCsvPoints.Count < 20)
                {
                    MessageBox.Show("Not enough points in the log!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                ApplyTrimAndRecalculate();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading the file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Loads unique, nonzero magnetometer points from a CSV file and applies the specified offsets.
        /// </summary>
        /// <param name="filePath">The path to the CSV file.</param>
        /// <param name="offsetX">The offset to add to each point's X coordinate.</param>
        /// <param name="offsetY">The offset to add to each point's Y coordinate.</param>
        /// <param name="offsetZ">The offset to add to each point's Z coordinate.</param>
        /// <returns>The unique, nonzero magnetometer points with the specified offsets applied.</returns>
        /// <exception cref="Exception">The required magADC[0], magADC[1], or magADC[2] column is missing from the CSV file.</exception>
        private List<Vector3> LoadUniqueMagPoints(string filePath, float offsetX, float offsetY, float offsetZ)
        {
            var uniquePoints = new HashSet<Vector3>();
            int idxX = -1, idxY = -1, idxZ = -1;

            using (var reader = new StreamReader(filePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');

                    if (idxX == -1)
                    {
                        for (int i = 0; i < parts.Length; i++)
                        {
                            string col = parts[i].Trim().Trim('"');
                            if (col == "magADC[0]") idxX = i;
                            else if (col == "magADC[1]") idxY = i;
                            else if (col == "magADC[2]") idxZ = i;
                        }
                        continue;
                    }

                    if (parts.Length > Math.Max(idxX, Math.Max(idxY, idxZ)))
                    {
                        if (float.TryParse(parts[idxX].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float x) &&
                            float.TryParse(parts[idxY].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float y) &&
                            float.TryParse(parts[idxZ].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out float z))
                        {
                            if (x == 0 && y == 0 && z == 0) continue;
                            uniquePoints.Add(new Vector3(x + offsetX, y + offsetY, z + offsetZ));
                        }
                    }
                }
            }

            if (idxX == -1 || idxY == -1 || idxZ == -1)
                throw new Exception("The magADC[0..2] columns were not found in the CSV file.");

            return uniquePoints.ToList();
        }

        /// <summary>
        /// Displays the number of unique points, principal-axis radii, and correction error.
        /// </summary>
        /// <remarks>The error is calculated using either center-only adjustment or the full soft-iron
        /// correction, according to the selected mode. The display is unchanged when there are no points.</remarks>
        private void UpdateStatsDisplay()
        {
            if (rawPoints == null || rawPoints.Count == 0) return;

            double sumSqDiff = 0;
            foreach (var p in rawPoints)
            {
                Vector3 evaluatedPoint;
                if (chkCenterOnly.Checked)
                {
                    // Center offset only (standard Betaflight)
                    evaluatedPoint = p - sphereCenter;
                }
                else
                {
                    // Full correction (center offset + 3x3 matrix)
                    evaluatedPoint = TransformVec3(softIronMatrix, p - sphereCenter);
                }

                double dist = evaluatedPoint.Length;
                sumSqDiff += Math.Pow(dist - sphereRadius, 2);
            }

            double errorPercent = (Math.Sqrt(sumSqDiff / rawPoints.Count) / sphereRadius) * 100.0;
            string modeLabel = chkCenterOnly.Checked ? "center only" : "full correction";

            lblStats.Text = $"Unique points: {rawPoints.Count}\n" +
                            $"Principal-axis radii: {lastRadii.X:F0} / {lastRadii.Y:F0} / {lastRadii.Z:F0}\n" +
                            $"Deviation ({modeLabel}): {errorPercent:F2} %" +
                            CompareEllipsoids();
        }
        /// <summary>
        /// Generates Betaflight CLI calibration commands and displays them in the CLI output field.
        /// </summary>
        /// <remarks>Includes the rounded hard-iron calibration values, the soft-iron correction matrix
        /// formatted with invariant culture, and the matrix coefficients scaled by 1000, followed by the save
        /// command.</remarks>
        private void UpdateCliOutput()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // Local helper for clean integer rounding without "-0"
            int RoundClean(double val) => (int)Math.Round(val) + 0;

            int cx = RoundClean(sphereCenter.X);
            int cy = RoundClean(sphereCenter.Y);
            int cz = RoundClean(sphereCenter.Z);

            int m00 = RoundClean(softIronMatrix.Row0.X * 1000);
            int m01 = RoundClean(softIronMatrix.Row0.Y * 1000);
            int m02 = RoundClean(softIronMatrix.Row0.Z * 1000);
            int m10 = RoundClean(softIronMatrix.Row1.X * 1000);
            int m11 = RoundClean(softIronMatrix.Row1.Y * 1000);
            int m12 = RoundClean(softIronMatrix.Row1.Z * 1000);
            int m20 = RoundClean(softIronMatrix.Row2.X * 1000);
            int m21 = RoundClean(softIronMatrix.Row2.Y * 1000);
            int m22 = RoundClean(softIronMatrix.Row2.Z * 1000);

            txtCliOutput.Text =
                $"# 1. Betaflight 2026.6+ CLI (Hard-Iron):\r\n" +
                $"set mag_calibration = {cx},{cy},{cz}\r\n\r\n" +
                $"# 2. 3x3 correction matrix (Soft-Iron - float):\r\n" +
                $"# [{softIronMatrix.Row0.X.ToString("F5", inv)}, {softIronMatrix.Row0.Y.ToString("F5", inv)}, {softIronMatrix.Row0.Z.ToString("F5", inv)}]\r\n" +
                $"# [{softIronMatrix.Row1.X.ToString("F5", inv)}, {softIronMatrix.Row1.Y.ToString("F5", inv)}, {softIronMatrix.Row1.Z.ToString("F5", inv)}]\r\n" +
                $"# [{softIronMatrix.Row2.X.ToString("F5", inv)}, {softIronMatrix.Row2.Y.ToString("F5", inv)}, {softIronMatrix.Row2.Z.ToString("F5", inv)}]\r\n\r\n" +
                $"# 3. Custom Betaflight CLI (Soft-Iron 3x3, x1000):\r\n" +
                $"set mag_softiron = {m00},{m01},{m02},{m10},{m11},{m12},{m20},{m21},{m22}\r\n" +
                $"save";
        }
        /// <summary>
        /// Fits an ellipsoid to the trimmed CSV points, updates calibration results and statistics, and refreshes the
        /// display.
        /// </summary>
        /// <remarks>Recalculation is skipped if fewer than 20 points remain after trimming.</remarks>
        private void ApplyTrimAndRecalculate()
        {
            if (allCsvPoints == null || allCsvPoints.Count < 20) return;

            int total = allCsvPoints.Count;
            int startIndex = (int)(total * ((double)numTrimStart.Value / 100.0));
            int endIndex = total - (int)(total * ((double)numTrimEnd.Value / 100.0));

            if (endIndex - startIndex < 20) return;

            Vector3 oldOffset = new Vector3(
                (float)numOldX.Value,
                (float)numOldY.Value,
                (float)numOldZ.Value
            );

            rawPoints.Clear();
            for (int i = startIndex; i < endIndex; i++)
            {
                rawPoints.Add(allCsvPoints[i] + oldOffset);
            }

            var fit = FitEllipsoid(rawPoints);
            sphereCenter = fit.Center;
            softIronMatrix = fit.SoftIronMatrix;
            sphereRadius = (fit.Radii.X + fit.Radii.Y + fit.Radii.Z) / 3.0f;
            ellipsoidAxes = fit.PrincipalAxes;
            lastRadii = fit.Radii;

            // -----------------------------------
            // Data from the second measured set for comparison
            rawPoints2.Clear();
            if (allCsvPoints2 != null && allCsvPoints2.Count >= 20)
            {
                int total2 = allCsvPoints2.Count;
                int start2 = (int)(total2 * ((double)numTrimStart.Value / 100.0));
                int end2 = total2 - (int)(total2 * ((double)numTrimEnd.Value / 100.0));

                for (int i = start2; i < end2; i++)
                {
                    rawPoints2.Add(allCsvPoints2[i] + oldOffset);
                }
            }
            // -----------------------------------

            UpdateStatsDisplay();

            UpdateCliOutput();

            glControl.Invalidate();
        }

        /// <summary>
        /// Initializes the OpenGL rendering state and axis-label textures, then updates the control's size-dependent
        /// settings.
        /// </summary>
        /// <param name="sender">The object that raised the event.</param>
        /// <param name="e">The event data.</param>
        private void GlControl_Load(object sender, EventArgs e)
        {
            glControl.MakeCurrent();
            GL.ClearColor(new Color4(25, 28, 34, 255));
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.PointSmooth);

            texLabelX = CreateTextTexture("X", Color.FromArgb(255, 80, 80));
            texLabelY = CreateTextTexture("Y", Color.FromArgb(80, 255, 80));
            texLabelZ = CreateTextTexture("Z", Color.FromArgb(80, 150, 255));

            GlControl_Resize(sender, e);
        }

        private void GlControl_Resize(object sender, EventArgs e)
        {
            if (glControl.Width <= 0 || glControl.Height <= 0) return;

            glControl.MakeCurrent();
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            float aspect = (float)glControl.Width / glControl.Height;
            Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.1f, 100f);
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadMatrix(ref projection);
        }

        /// <summary>
        /// Renders the current calibration visualization in the OpenGL control, including the scene transform, axes,
        /// normalized measurement points, and overlay annotations.
        /// </summary>
        /// <remarks>The control context is made current, the color and depth buffers are cleared, the
        /// model-view transform is applied, and the scene is redrawn with the current calibration state. The method
        /// draws the axis markers, plots the normalized point cloud, optionally overlays the secondary dataset, and
        /// updates the display by swapping the front and back buffers.</remarks>
        /// <param name="sender">The source of the paint event.</param>
        /// <param name="e">The paint event data.</param>
        private void GlControl_Paint(object sender, PaintEventArgs e)
        {
            glControl.MakeCurrent();
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            Matrix4 view = Matrix4.CreateTranslation(0f, 0f, -zoom);
            Matrix4 model = Matrix4.CreateFromQuaternion(sceneRotation);
            Matrix4 modelView = model * view;

            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadMatrix(ref modelView);
            DrawAxes(1.2f);

            if (rawPoints.Count > 0 && sphereRadius > 0)
            {
                float scale = 1.0f / sphereRadius;

                GL.Color4(0.35f, 0.35f, 0.4f, 0.5f);
                DrawWireSphere(1.0f, 24, 16);

                GL.PointSize(4.0f);
                GL.Begin(PrimitiveType.Points);
                foreach (var p in rawPoints)
                {
                    Vector3 normP; // zohledneni dvou checkboxu pro styl vykresleni
                    if (chkApplyEllipsoid.Checked)
                    {
                        // FULL CORRECTION: centered and transformed into a sphere by the 3x3 matrix
                        normP = TransformVec3(softIronMatrix, p - sphereCenter) * scale;
                    }
                    else if (chkCenterOnly.Checked)
                    {
                        // CENTER ONLY (Betaflight): centered, but remains ellipsoidal
                        normP = (p - sphereCenter) * scale;
                    }
                    else
                    {
                        // RAW DATA: no correction
                        normP = p * scale;
                    }

                    float err = Math.Abs(normP.Length - 1.0f) * 10f;
                    err = Math.Clamp(err, 0f, 1f);
                    GL.Color3(err, 1.0f - err, 0.2f);

                    GL.Vertex3(normP.X, normP.Y, normP.Z);
                }
                GL.End();

                // ---------------------------------------------------------------------------------

                // Draw points from the second file (cyan for easy correlation comparison)
                if (chkShowCsv2.Checked && rawPoints2 != null && rawPoints2.Count > 0)
                {
                    GL.PointSize(2.0f); // smaller points for better overlay readability
                    GL.Color3(0.0f, 0.85f, 1.0f); // bright cyan
                    GL.Begin(PrimitiveType.Points);

                    foreach (var p2 in rawPoints2)
                    {
                        Vector3 normP2;
                        if (chkApplyEllipsoid.Checked)
                        {
                            normP2 = TransformVec3(softIronMatrix, p2 - sphereCenter) * scale;
                        }
                        else if (chkCenterOnly.Checked)
                        {
                            normP2 = (p2 - sphereCenter) * scale;
                        }
                        else
                        {
                            normP2 = p2 * scale;
                        }

                        GL.Vertex3(normP2.X, normP2.Y, normP2.Z);
                    }

                    GL.End();
                }

                // Determine the calculated center position (offset by sphereCenter without correction, at 0,0,0 after correction)
                Vector3 crossPos = (chkApplyEllipsoid.Checked || chkCenterOnly.Checked) ? Vector3.Zero : (sphereCenter * scale);

                DrawCenterCross(crossPos, 0.15f);
                DrawEllipsoidAxes(crossPos, ellipsoidAxes, scale); // draw the ellipsoid's rotated principal axes

            }

            DrawAxisLabels(1.32f);

            glControl.SwapBuffers();
        }

        /// <summary>
        /// Draws the X, Y, and Z coordinate axes centered at the origin.
        /// </summary>
        /// <remarks>The X, Y, and Z axes are drawn in red, green, and blue, respectively.</remarks>
        /// <param name="length">The distance from the origin to each axis endpoint.</param>
        private void DrawAxes(float length)
        {
            GL.LineWidth(2.0f);
            GL.Begin(PrimitiveType.Lines);

            GL.Color3(1.0f, 0.2f, 0.2f);
            GL.Vertex3(-length, 0, 0);
            GL.Vertex3(length, 0, 0);

            GL.Color3(0.2f, 1.0f, 0.2f);
            GL.Vertex3(0, -length, 0);
            GL.Vertex3(0, length, 0);

            GL.Color3(0.2f, 0.5f, 1.0f);
            GL.Vertex3(0, 0, -length);
            GL.Vertex3(0, 0, length);

            GL.End();
        }
        /// <summary>
        /// Draws a yellow three-axis cross and a point at the specified center.
        /// </summary>
        /// <param name="center">The center point of the cross.</param>
        /// <param name="armLength">The distance from the center to each endpoint of the cross arms.</param>
        private void DrawCenterCross(Vector3 center, float armLength)
        {
            // 1. Yellow 3D cross (three perpendicular segments intersecting at center)
            GL.LineWidth(4.5f);
            GL.Color3(1.0f, 0.9f, 0.1f); // bright yellow

            GL.Begin(PrimitiveType.Lines);
            // Cross X axis
            GL.Vertex3(center.X - armLength, center.Y, center.Z);
            GL.Vertex3(center.X + armLength, center.Y, center.Z);
            // Cross Y axis
            GL.Vertex3(center.X, center.Y - armLength, center.Z);
            GL.Vertex3(center.X, center.Y + armLength, center.Z);
            // Cross Z axis
            GL.Vertex3(center.X, center.Y, center.Z - armLength);
            GL.Vertex3(center.X, center.Y, center.Z + armLength);
            GL.End();

            // 2. Highlighted point exactly at the cross intersection
            GL.PointSize(8.0f);
            GL.Color3(1.0f, 1.0f, 0.4f); // light yellow point
            GL.Begin(PrimitiveType.Points);
            GL.Vertex3(center.X, center.Y, center.Z);
            GL.End();

            GL.LineWidth(1.0f);
        }

        /// <summary>
        /// Draws a wireframe sphere centered at the origin.
        /// </summary>
        /// <param name="r">The radius of the sphere.</param>
        /// <param name="slices">The number of longitudinal divisions.</param>
        /// <param name="stacks">The number of latitudinal divisions.</param>
        private void DrawWireSphere(float r, int slices, int stacks)
        {
            GL.LineWidth(1.0f);
            for (int i = 1; i < stacks; i++)
            {
                float lat = MathF.PI * (-0.5f + (float)i / stacks);
                float y = r * MathF.Sin(lat);
                float zr = r * MathF.Cos(lat);

                GL.Begin(PrimitiveType.LineLoop);
                for (int j = 0; j < slices; j++)
                {
                    float lng = 2 * MathF.PI * j / slices;
                    GL.Vertex3(zr * MathF.Cos(lng), y, zr * MathF.Sin(lng));
                }
                GL.End();
            }
            for (int j = 0; j < slices; j++)
            {
                float lng = 2 * MathF.PI * j / slices;
                float cosLng = MathF.Cos(lng);
                float sinLng = MathF.Sin(lng);

                GL.Begin(PrimitiveType.LineStrip);
                for (int i = 0; i <= stacks; i++)
                {
                    float lat = MathF.PI * (-0.5f + (float)i / stacks);
                    GL.Vertex3(r * MathF.Cos(lat) * cosLng, r * MathF.Sin(lat), r * MathF.Cos(lat) * sinLng);
                }
                GL.End();
            }
        }

        /// <summary>
        /// Draws the three ellipsoid axes and marks their endpoints.
        /// </summary>
        /// <param name="center">The center point of the ellipsoid axes.</param>
        /// <param name="axes">The three direction vectors defining the ellipsoid axes.</param>
        /// <param name="scale">The scale applied to each axis direction.</param>
        private void DrawEllipsoidAxes(Vector3 center, Vector3[] axes, float scale)
        {
            if (axes == null || axes.Length < 3) return;

            GL.LineWidth(3.0f);
            GL.Color3(1.0f, 0.2f, 0.9f); // vivid magenta to distinguish the axes from X, Y, and Z

            GL.Begin(PrimitiveType.Lines);
            for (int i = 0; i < 3; i++)
            {
                Vector3 dir = chkApplyEllipsoid.Checked
                    ? TransformVec3(softIronMatrix, axes[i]) * scale
                    : axes[i] * scale;

                Vector3 p1 = center - dir;
                Vector3 p2 = center + dir;

                GL.Vertex3(p1.X, p1.Y, p1.Z);
                GL.Vertex3(p2.X, p2.Y, p2.Z);
            }
            GL.End();

            // Highlight the endpoints of the principal axes
            GL.PointSize(7.0f);
            GL.Color3(1.0f, 0.6f, 1.0f);
            GL.Begin(PrimitiveType.Points);
            for (int i = 0; i < 3; i++)
            {
                Vector3 dir = chkApplyEllipsoid.Checked
                    ? TransformVec3(softIronMatrix, axes[i]) * scale
                    : axes[i] * scale;

                Vector3 p1 = center - dir;
                Vector3 p2 = center + dir;

                GL.Vertex3(p1.X, p1.Y, p1.Z);
                GL.Vertex3(p2.X, p2.Y, p2.Z);
            }
            GL.End();

            GL.LineWidth(1.0f);
        }

        /// <summary>
        /// Renders labels at the positive ends of the X, Y, and Z axes.
        /// </summary>
        /// <param name="distance">The distance from the origin at which to render each axis label.</param>
        private void DrawAxisLabels(float distance)
        {
            // Enable transparency (alpha blending) for smooth text edges
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Enable(EnableCap.Texture2D);

            // Draw labels at the ends of the axes
            DrawBillboardText(texLabelX, new Vector3(distance, 0, 0), 0.18f);
            DrawBillboardText(texLabelY, new Vector3(0, distance, 0), 0.18f);
            DrawBillboardText(texLabelZ, new Vector3(0, 0, distance), 0.18f);

            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.Blend);
        }

        
        /// <summary>
        /// Draws a camera-facing text billboard at the specified position.
        /// </summary>
        /// <param name="textureId">The OpenGL texture identifier to draw.</param>
        /// <param name="position">The position of the billboard in the scene.</param>
        /// <param name="size">The width and height of the billboard.</param>
        private void DrawBillboardText(int textureId, Vector3 position, float size)
        {
            GL.PushMatrix();
            GL.Translate(position);

            // Cancel camera rotation (billboarding) using the inverse quaternion
            Quaternion invRot = Quaternion.Invert(sceneRotation);
            Matrix4 invMatrix = Matrix4.CreateFromQuaternion(invRot);
            GL.MultMatrix(ref invMatrix);

            GL.BindTexture(TextureTarget.Texture2D, textureId);
            GL.Color4(1f, 1f, 1f, 1f); // white preserves the texture's original color

            float half = size * 0.5f;
            GL.Begin(PrimitiveType.Quads);
            GL.TexCoord2(0, 1); GL.Vertex3(-half, -half, 0);
            GL.TexCoord2(1, 1); GL.Vertex3(half, -half, 0);
            GL.TexCoord2(1, 0); GL.Vertex3(half, half, 0);
            GL.TexCoord2(0, 0); GL.Vertex3(-half, half, 0);
            GL.End();

            GL.PopMatrix();
        }

        /// <summary>
        /// Creates an OpenGL texture containing centered, anti-aliased text.
        /// </summary>
        /// <param name="text">The text to render.</param>
        /// <param name="textColor">The color of the rendered text.</param>
        /// <returns>The identifier of the generated OpenGL texture.</returns>
        private int CreateTextTexture(string text, Color textColor)
        {
            int size = 128; // texture resolution (128x128 px for sharp text)
            using (Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                using (Font font = new Font("Segoe UI", 64, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush brush = new SolidBrush(textColor))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), sf);
                }

                // Upload the bitmap to graphics memory as an OpenGL texture
                int texId = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, texId);

                var data = bmp.LockBits(
                    new Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                    data.Width, data.Height, 0,
                    OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);

                bmp.UnlockBits(data);

                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

                return texId;
            }
        }

        /// <summary>
        /// Maps a mouse position to a point on the virtual trackball.
        /// </summary>
        /// <param name="mousePos">The mouse position in the OpenGL control's client coordinates.</param>
        /// <returns>A point on the unit sphere representing the mapped mouse position.</returns>
        private Vector3 MapToTrackball(Point mousePos)
        {
            // Normalize mouse coordinates to the range [-1.0, 1.0] (the window center is 0)
            float width = Math.Max(1, glControl.Width);
            float height = Math.Max(1, glControl.Height);
            float minDim = Math.Min(width, height);

            // Divide by the smaller window dimension so the sphere is not flattened in a rectangular window
            float x = (2.0f * mousePos.X - width) / minDim;
            float y = (height - 2.0f * mousePos.Y) / minDim; // OpenGL's Y axis points upward

            float lengthSq = x * x + y * y;
            float z;

            if (lengthSq <= 1.0f)
            {
                // The point is inside the circle, so project it directly onto the sphere surface (x^2 + y^2 + z^2 = 1)
                z = (float)Math.Sqrt(1.0f - lengthSq);
            }
            else
            {
                // The point is outside the circle (in a window corner), so project it onto the equator (z = 0)
                float norm = 1.0f / (float)Math.Sqrt(lengthSq);
                x *= norm;
                y *= norm;
                z = 0.0f;
            }

            return new Vector3(x, y, z);
        }
        
        /// <summary>
        /// Begins trackball dragging when the left mouse button is pressed.
        /// </summary>
        /// <param name="sender">The object that raised the event.</param>
        /// <param name="e">The mouse event data.</param>
        private void GlControl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging = true;
                trackballLastVec = MapToTrackball(e.Location);
            }
        }
        /// <summary>
        /// Updates the scene rotation as the pointer moves during a drag.
        /// </summary>
        /// <param name="sender">The source of the mouse-move event.</param>
        /// <param name="e">The mouse event data containing the pointer location.</param>
        private void GlControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging) return;

            Vector3 currentVec = MapToTrackball(e.Location);

            // The rotation axis is perpendicular to both vectors (cross product)
            Vector3 axis = Vector3.Cross(trackballLastVec, currentVec);

            if (axis.LengthSquared > 1e-7f)
            {
                // Angle between the vectors
                float dot = Math.Clamp(Vector3.Dot(trackballLastVec, currentVec), -1.0f, 1.0f);
                float angle = (float)Math.Acos(dot);

                // Build the incremental quaternion (the 1.5f factor speeds up rotation for easier control)
                Quaternion deltaRot = Quaternion.FromAxisAngle(axis.Normalized(), angle * 1.5f);

                // Multiply from the left so rotation always occurs in screen coordinates (the camera view)
                sceneRotation = Quaternion.Normalize(deltaRot * sceneRotation);
            }

            trackballLastVec = currentVec;
            glControl.Invalidate();
        }
        /// <summary>
        /// Ends dragging when the left mouse button is released.
        /// </summary>
        /// <param name="sender">The source of the mouse event.</param>
        /// <param name="e">The mouse event data.</param>
        private void GlControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging = false;
            }
        }
        /// <summary>
        /// Adjusts the zoom level based on the mouse wheel and redraws the control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse wheel event data.</param>
        private void GlControl_MouseWheel(object sender, MouseEventArgs e)
        {
            zoom -= e.Delta * 0.0015f;
            zoom = Math.Clamp(zoom, 0.5f, 10.0f);
            glControl.Invalidate();
        }

        /// <summary>
        /// Generates simulated measurement points, fits an ellipsoid to them, and updates the calibration results and
        /// display.
        /// </summary>
        /// <param name="pointCount">The number of simulated measurement points to generate.</param>
        private void GenerateSimulatedData(int pointCount)
        {
            Random rnd = new Random();
            rawPoints.Clear();

            // Add below the trueRadius definition:
            Vector3 simDeformation = new Vector3(1.15f, 1.0f, 0.80f); // axis deformation (X +15%, Z -20%)

            // Simulated true center (sensor offset) and radius (magnetic field strength)
            Vector3 trueCenter = new Vector3(185f, -240f, 95f);
            float trueRadius = 420f;
            float noiseLevel = 26.0f; // realistic IST8310 sensor noise is approximately 6.0f

            for (int i = 0; i < pointCount; i++)
            {
                // Uniform random distribution of points on the sphere surface (spherical coordinates)
                double u = rnd.NextDouble();
                double v = rnd.NextDouble();
                double theta = 2.0 * Math.PI * u;
                double phi = Math.Acos(2.0 * v - 1.0);

                // Ellipsoid simulation:
                float x = (float)(trueRadius * simDeformation.X * Math.Sin(phi) * Math.Cos(theta));
                float y = (float)(trueRadius * simDeformation.Y * Math.Sin(phi) * Math.Sin(theta));
                float z = (float)(trueRadius * simDeformation.Z * Math.Cos(phi));

                // Add a small amount of noise to each axis
                float nx = (float)((rnd.NextDouble() * 2.0 - 1.0) * noiseLevel);
                float ny = (float)((rnd.NextDouble() * 2.0 - 1.0) * noiseLevel);
                float nz = (float)((rnd.NextDouble() * 2.0 - 1.0) * noiseLevel);

                rawPoints.Add(new Vector3(
                    trueCenter.X + x + nx,
                    trueCenter.Y + y + ny,
                    trueCenter.Z + z + nz
                ));
            }

            // Run the same calculation and update the UI as when loading a CSV
            // general ellipsoid code with arbitrary rotation
            var fit = FitEllipsoid(rawPoints);
            sphereCenter = fit.Center;
            softIronMatrix = fit.SoftIronMatrix;
            sphereRadius = (fit.Radii.X + fit.Radii.Y + fit.Radii.Z) / 3.0f;

            ellipsoidAxes = fit.PrincipalAxes;

            lastRadii = fit.Radii;

            // Calculate the error after fitting the rotated ellipsoid to a sphere
            double sumSqDiff = 0;
            foreach (var p in rawPoints)
            {
                Vector3 corrected = TransformVec3(softIronMatrix, p - sphereCenter);
                double dist = corrected.Length;
                sumSqDiff += Math.Pow(dist - sphereRadius, 2);
            }
            
            UpdateStatsDisplay(); // replaces the commented-out code above

            UpdateCliOutput();

            glControl.Invalidate();
        }

        /// <summary>
        /// Compares two ellipsoid fits using their center offset, longest-axis angle, shape ratio, and
        /// cross-calibration error.
        /// </summary>
        /// <returns>A formatted comparison report, or an empty string if either point set contains fewer than 20 points. Returns
        /// an error message if the second ellipsoid cannot be fitted.</returns>
        private string CompareEllipsoids()
        {
            if (rawPoints == null || rawPoints.Count < 20 || rawPoints2 == null || rawPoints2.Count < 20)
                return string.Empty;

            try
            {
                var fit2 = FitEllipsoid(rawPoints2);

                // 1. Center offset (Hard-Iron difference)
                Vector3 centerDiff = fit2.Center - sphereCenter;
                float centerDist = centerDiff.Length;

                // 2. Shape comparison (longest / shortest semi-axis ratio)
                float max1 = Math.Max(lastRadii.X, Math.Max(lastRadii.Y, lastRadii.Z));
                float min1 = Math.Min(lastRadii.X, Math.Min(lastRadii.Y, lastRadii.Z));
                float ratio1 = max1 / min1;

                float max2 = Math.Max(fit2.Radii.X, Math.Max(fit2.Radii.Y, fit2.Radii.Z));
                float min2 = Math.Min(fit2.Radii.X, Math.Min(fit2.Radii.Y, fit2.Radii.Z));
                float ratio2 = max2 / min2;
                float shapeDiffPercent = Math.Abs(ratio1 - ratio2) * 100.0f;

                // 3. Angular rotation of the longest principal axis (0° to 90°)
                Vector3 mainAxis1 = GetLongestAxis(ellipsoidAxes).Normalized();
                Vector3 mainAxis2 = GetLongestAxis(fit2.PrincipalAxes).Normalized();
                float dot = Math.Clamp(Math.Abs(Vector3.Dot(mainAxis1, mainAxis2)), 0.0f, 1.0f);
                double angleDeg = Math.Acos(dot) * (180.0 / Math.PI);

                // 4. Cross-error: how well the second set fits the first set's calibration
                double sumSqCross = 0;
                foreach (var p2 in rawPoints2)
                {
                    Vector3 corrected = TransformVec3(softIronMatrix, p2 - sphereCenter);
                    sumSqCross += Math.Pow(corrected.Length - sphereRadius, 2);
                }
                double crossErrorPercent = (Math.Sqrt(sumSqCross / rawPoints2.Count) / sphereRadius) * 100.0;

                return $"\n--- COMPARISON OF SETS 1 AND 2 ---\n" +
                       $"Center offset: {centerDist:F1} ({centerDiff.X:+0;-0;0}, {centerDiff.Y:+0;-0;0}, {centerDiff.Z:+0;-0;0})\n" +
                       $"Principal-axis rotation: {angleDeg:F1}°\n" +
                       $"Shape difference: {shapeDiffPercent:F2}% (ratio {ratio1:F2} vs {ratio2:F2})\n" +
                       $"Set 2 error with set 1 calibration: {crossErrorPercent:F2}%";
            }
            catch
            {
                return "\n--- COMPARISON OF SETS 1 AND 2 ---\nUnable to fit an ellipsoid to set 2.";
            }
        }
        /// <summary>
        /// Gets the axis with the greatest length.
        /// </summary>
        /// <param name="axes">The axes to compare.</param>
        /// <returns>The axis with the greatest length, or <see cref="Vector3.Zero"/> if no axis has a positive length.</returns>
        private static Vector3 GetLongestAxis(Vector3[] axes)
        {
            Vector3 longest = Vector3.Zero;
            foreach (var axis in axes)
            {
                if (axis.LengthSquared > longest.LengthSquared)
                {
                    longest = axis;
                }
            }
            return longest;
        }

        /*
        private (double X0, double Y0, double Z0, double Radius) FitSphere(List<Vector3> points)
        {
            double[,] ATA = new double[4, 4];
            double[] ATf = new double[4];

            foreach (var p in points)
            {
                double[] row = { 2.0 * p.X, 2.0 * p.Y, 2.0 * p.Z, 1.0 };
                double f = (double)p.X * p.X + (double)p.Y * p.Y + (double)p.Z * p.Z;

                for (int i = 0; i < 4; i++)
                {
                    ATf[i] += row[i] * f;
                    for (int j = 0; j < 4; j++) ATA[i, j] += row[i] * row[j];
                }
            }

            double[] C = SolveLinearSystem4x4(ATA, ATf);
            double x0 = C[0], y0 = C[1], z0 = C[2];
            double radius = Math.Sqrt(C[3] + x0 * x0 + y0 * y0 + z0 * z0);

            return (x0, y0, z0, radius);
        }

        private double[] SolveLinearSystem4x4(double[,] A, double[] B)
        {
            int n = 4;
            double[,] M = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) M[i, j] = A[i, j];
                M[i, n] = B[i];
            }

            for (int k = 0; k < n; k++)
            {
                int maxRow = k;
                for (int i = k + 1; i < n; i++)
                    if (Math.Abs(M[i, k]) > Math.Abs(M[maxRow, k])) maxRow = i;

                for (int j = k; j <= n; j++)
                {
                    double temp = M[k, j]; M[k, j] = M[maxRow, j]; M[maxRow, j] = temp;
                }

                for (int i = k + 1; i < n; i++)
                {
                    double factor = M[i, k] / M[k, k];
                    for (int j = k; j <= n; j++) M[i, j] -= factor * M[k, j];
                }
            }

            double[] x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = 0;
                for (int j = i + 1; j < n; j++) sum += M[i, j] * x[j];
                x[i] = (M[i, n] - sum) / M[i, i];
            }
            return x;
        }
        */

        /// <summary>
        /// Fits an ellipsoid to the supplied points and computes its center, radii, soft-iron correction matrix, and
        /// principal axes.
        /// </summary>
        /// <param name="points">The three-dimensional points used to fit the ellipsoid.</param>
        /// <returns>A tuple containing the ellipsoid center, radii, soft-iron correction matrix, and three principal axes.</returns>
        /// <exception cref="Exception">The fitted shape is not a closed ellipsoid because one or more eigenvalues are not positive.</exception>
        private (Vector3 Center, Vector3 Radii, Matrix3 SoftIronMatrix, Vector3[] PrincipalAxes) FitEllipsoid(List<Vector3> points)
        {
            // 1. Build the 9x9 normal equations:
            // a0*x^2 + a1*y^2 + a2*z^2 + a3*xy + a4*xz + a5*yz + a6*x + a7*y + a8*z = 1
            double[,] ATA = new double[9, 9];
            double[] ATb = new double[9];

            foreach (var p in points)
            {
                double x = p.X, y = p.Y, z = p.Z;
                double[] row = { x * x, y * y, z * z, x * y, x * z, y * z, x, y, z };

                for (int i = 0; i < 9; i++)
                {
                    ATb[i] += row[i];
                    for (int j = 0; j < 9; j++)
                        ATA[i, j] += row[i] * row[j];
                }
            }

            double[] a = SolveLinearSystem(ATA, ATb, 9);

            // 2. Quadratic matrix Q (3x3) and linear vector u (3x1)
            double[,] Q = {
                            { a[0],       a[3] * 0.5, a[4] * 0.5 },
                            { a[3] * 0.5, a[1],       a[5] * 0.5 },
                            { a[4] * 0.5, a[5] * 0.5, a[2]       }
                          };
            double[] negU = { -a[6] * 0.5, -a[7] * 0.5, -a[8] * 0.5 };

            // Ellipsoid center (Hard-Iron offset): Q * center = -u
            double[] c = SolveLinearSystem(Q, negU, 3);
            Vector3 center = new Vector3((float)c[0], (float)c[1], (float)c[2]);

            // Right-hand side after translating to the center: rhs = 1 - c^T * u
            double cDotU = c[0] * (a[6] * 0.5) + c[1] * (a[7] * 0.5) + c[2] * (a[8] * 0.5);
            double rhs = 1.0 - cDotU;

            // Normalized matrix M = Q / rhs, so (p - c)^T * M * (p - c) = 1
            double[,] M = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    M[i, j] = Q[i, j] / rhs;

            // 3. Eigenvalues and eigenvectors of the symmetric matrix M
            JacobiEigen3x3(M, out double[] eigVal, out double[,] V);

            // Verify that this is an actual ellipsoid (all eigenvalues must be positive)
            if (eigVal[0] <= 0 || eigVal[1] <= 0 || eigVal[2] <= 0)
                throw new Exception("The data cannot define a closed ellipsoid (insufficient spatial coverage).");

            // Radii along the rotated principal ellipsoid axes
            double r0 = 1.0 / Math.Sqrt(eigVal[0]);
            double r1 = 1.0 / Math.Sqrt(eigVal[1]);
            double r2 = 1.0 / Math.Sqrt(eigVal[2]);
            double avgRadius = (r0 + r1 + r2) / 3.0;

            // 4. Build the symmetric correction matrix W = V * diag(avgRadius / r_i) * V^T
            double[] d = { avgRadius / r0, avgRadius / r1, avgRadius / r2 };
            double[,] W = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    W[i, j] = V[i, 0] * d[0] * V[j, 0] +
                              V[i, 1] * d[1] * V[j, 1] +
                              V[i, 2] * d[2] * V[j, 2];
                }
            }

            Matrix3 softIron = new Matrix3(
                (float)W[0, 0], (float)W[0, 1], (float)W[0, 2],
                (float)W[1, 0], (float)W[1, 1], (float)W[1, 2],
                (float)W[2, 0], (float)W[2, 1], (float)W[2, 2]
            );

            //return (center, new Vector3((float)r0, (float)r1, (float)r2), softIron);

            // 5. Build the ellipsoid's principal semi-axes (unit eigenvector * corresponding radius)
            Vector3[] principalAxes = new Vector3[3];
            principalAxes[0] = new Vector3((float)V[0, 0], (float)V[1, 0], (float)V[2, 0]) * (float)r0;
            principalAxes[1] = new Vector3((float)V[0, 1], (float)V[1, 1], (float)V[2, 1]) * (float)r1;
            principalAxes[2] = new Vector3((float)V[0, 2], (float)V[1, 2], (float)V[2, 2]) * (float)r2;

            return (center, new Vector3((float)r0, (float)r1, (float)r2), softIron, principalAxes);
        }

        /// <summary>
        /// Solves the linear system Ax = B using Gaussian elimination with partial pivoting.
        /// </summary>
        /// <remarks>A must be an n-by-n matrix, B must contain n values, and the system must have a
        /// unique solution.</remarks>
        /// <param name="A">The n-by-n coefficient matrix.</param>
        /// <param name="B">The right-hand-side vector containing n values.</param>
        /// <param name="n">The number of equations and unknowns.</param>
        /// <returns>A vector containing the solution to the system.</returns>
        private double[] SolveLinearSystem(double[,] A, double[] B, int n)
        {
            double[,] M = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) M[i, j] = A[i, j];
                M[i, n] = B[i];
            }

            for (int k = 0; k < n; k++)
            {
                int maxRow = k;
                for (int i = k + 1; i < n; i++)
                    if (Math.Abs(M[i, k]) > Math.Abs(M[maxRow, k])) maxRow = i;

                for (int j = k; j <= n; j++)
                {
                    double temp = M[k, j]; M[k, j] = M[maxRow, j]; M[maxRow, j] = temp;
                }

                for (int i = k + 1; i < n; i++)
                {
                    double factor = M[i, k] / M[k, k];
                    for (int j = k; j <= n; j++) M[i, j] -= factor * M[k, j];
                }
            }

            double[] x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = 0;
                for (int j = i + 1; j < n; j++) sum += M[i, j] * x[j];
                x[i] = (M[i, n] - sum) / M[i, i];
            }
            return x;
        }


        /// <summary>
        /// Computes the eigenvalues and eigenvectors of a real symmetric 3-by-3 matrix using the Jacobi method.
        /// </summary>
        /// <remarks>Eigenvalues and corresponding eigenvectors are not sorted. The iteration stops when
        /// the largest off-diagonal element is below the convergence threshold or after 50 iterations.</remarks>
        /// <param name="A">The real symmetric 3-by-3 matrix to decompose.</param>
        /// <param name="d">When this method returns, contains the eigenvalues.</param>
        /// <param name="V">When this method returns, contains the corresponding eigenvectors as columns.</param>
        private static void JacobiEigen3x3(double[,] A, out double[] d, out double[,] V)
        {
            double[,] a = (double[,])A.Clone();
            V = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

            for (int iter = 0; iter < 50; iter++)
            {
                // Find the largest off-diagonal element
                int p = 0, q = 1;
                double maxVal = Math.Abs(a[0, 1]);
                if (Math.Abs(a[0, 2]) > maxVal) { p = 0; q = 2; maxVal = Math.Abs(a[0, 2]); }
                if (Math.Abs(a[1, 2]) > maxVal) { p = 1; q = 2; maxVal = Math.Abs(a[1, 2]); }

                if (maxVal < 1e-12) break;

                double diff = a[q, q] - a[p, p];
                double t;
                if (Math.Abs(diff) < 1e-15)
                {
                    t = 1.0;
                }
                else
                {
                    double phi = diff / (2.0 * a[p, q]);
                    t = 1.0 / (Math.Abs(phi) + Math.Sqrt(phi * phi + 1.0));
                    if (phi < 0) t = -t;
                }

                double c = 1.0 / Math.Sqrt(t * t + 1.0);
                double s = t * c;
                double tau = s / (1.0 + c);

                double h = t * a[p, q];
                a[p, p] -= h;
                a[q, q] += h;
                a[p, q] = a[q, p] = 0.0;

                for (int r = 0; r < 3; r++)
                {
                    if (r != p && r != q)
                    {
                        double arp = a[r, p];
                        double arq = a[r, q];
                        a[r, p] = a[p, r] = arp - s * (arq + tau * arp);
                        a[r, q] = a[q, r] = arq + s * (arp - tau * arq);
                    }
                    double vrp = V[r, p];
                    double vrq = V[r, q];
                    V[r, p] = vrp - s * (vrq + tau * vrp);
                    V[r, q] = vrq + s * (vrp - tau * vrq);
                }
            }

            d = new double[] { a[0, 0], a[1, 1], a[2, 2] };
        }

        /// <summary>
        /// Transforms a 3D vector by multiplying it with a 3x3 matrix.
        /// </summary>
        /// <param name="m">The 3x3 transformation matrix.</param>
        /// <param name="v">The vector to transform.</param>
        /// <returns>The transformed vector.</returns>
        private static Vector3 TransformVec3(Matrix3 m, Vector3 v)
        {
            return new Vector3(
                m.Row0.X * v.X + m.Row0.Y * v.Y + m.Row0.Z * v.Z,
                m.Row1.X * v.X + m.Row1.Y * v.Y + m.Row1.Z * v.Z,
                m.Row2.X * v.X + m.Row2.Y * v.Y + m.Row2.Z * v.Z
            );
        }

    }
}




