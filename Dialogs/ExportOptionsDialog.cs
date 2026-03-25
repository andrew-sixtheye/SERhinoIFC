using System;
using Eto.Drawing;
using Eto.Forms;
using SERhinoIFC.Export;

namespace SERhinoIFC.Dialogs
{
    public class ExportOptionsDialog : Dialog<ExportOptions>
    {
        private readonly DropDown _exportModeDropDown;
        private readonly DropDown _unitsDropDown;
        private readonly TextBox _toleranceTextBox;
        private readonly DropDown _ifcSchemaDropDown;
        private readonly TextBox _authorTextBox;
        private readonly TextBox _organizationTextBox;

        public ExportOptionsDialog(Rhino.UnitSystem rhinoUnits, double rhinoTolerance)
        {
            Title = "IFC Export Options";
            Padding = new Padding(15);
            MinimumSize = new Size(400, 0);
            Resizable = false;

            // Export Mode
            _exportModeDropDown = new DropDown();
            _exportModeDropDown.Items.Add("General IFC (Brep)");
            _exportModeDropDown.Items.Add("SE-Cbot (Solid)");
            _exportModeDropDown.SelectedIndex = 0;

            // Units
            _unitsDropDown = new DropDown();
            _unitsDropDown.Items.Add("Millimeters");
            _unitsDropDown.Items.Add("Centimeters");
            _unitsDropDown.Items.Add("Meters");
            _unitsDropDown.Items.Add("Inches");
            _unitsDropDown.Items.Add("Feet");

            // Default to match Rhino doc units
            switch (rhinoUnits)
            {
                case Rhino.UnitSystem.Millimeters: _unitsDropDown.SelectedIndex = 0; break;
                case Rhino.UnitSystem.Centimeters: _unitsDropDown.SelectedIndex = 1; break;
                case Rhino.UnitSystem.Meters:      _unitsDropDown.SelectedIndex = 2; break;
                case Rhino.UnitSystem.Inches:      _unitsDropDown.SelectedIndex = 3; break;
                case Rhino.UnitSystem.Feet:        _unitsDropDown.SelectedIndex = 4; break;
                default:                           _unitsDropDown.SelectedIndex = 2; break; // meters fallback
            }

            // Tolerance
            _toleranceTextBox = new TextBox { Text = rhinoTolerance.ToString("G") };

            // IFC Schema
            _ifcSchemaDropDown = new DropDown();
            _ifcSchemaDropDown.Items.Add("IFC2x3");
            _ifcSchemaDropDown.Items.Add("IFC4");
            _ifcSchemaDropDown.SelectedIndex = 0;

            // Author
            _authorTextBox = new TextBox { Text = Environment.UserName };

            // Organization
            _organizationTextBox = new TextBox();

            // Buttons
            var okButton = new Button { Text = "OK" };
            okButton.Click += OnOkClick;

            var cancelButton = new Button { Text = "Cancel" };
            cancelButton.Click += (s, e) =>
            {
                Result = null;
                Close();
            };

            DefaultButton = okButton;
            AbortButton = cancelButton;

            // Layout
            var formLayout = new TableLayout
            {
                Spacing = new Size(10, 8),
                Rows =
                {
                    new TableRow(
                        new Label { Text = "Export Mode", VerticalAlignment = VerticalAlignment.Center },
                        _exportModeDropDown
                    ),
                    new TableRow(
                        new Label { Text = "IFC Units", VerticalAlignment = VerticalAlignment.Center },
                        _unitsDropDown
                    ),
                    new TableRow(
                        new Label { Text = "Tolerance", VerticalAlignment = VerticalAlignment.Center },
                        _toleranceTextBox
                    ),
                    new TableRow(
                        new Label { Text = "IFC Schema", VerticalAlignment = VerticalAlignment.Center },
                        _ifcSchemaDropDown
                    ),
                    new TableRow(
                        new Label { Text = "Author", VerticalAlignment = VerticalAlignment.Center },
                        _authorTextBox
                    ),
                    new TableRow(
                        new Label { Text = "Organization", VerticalAlignment = VerticalAlignment.Center },
                        _organizationTextBox
                    ),
                }
            };

            var buttonLayout = new TableLayout
            {
                Spacing = new Size(5, 0),
                Rows =
                {
                    new TableRow(null, okButton, cancelButton)
                }
            };

            Content = new StackLayout
            {
                Spacing = 15,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    formLayout,
                    buttonLayout
                }
            };
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            ExportUnitSystem units;
            switch (_unitsDropDown.SelectedIndex)
            {
                case 0: units = ExportUnitSystem.Millimeters; break;
                case 1: units = ExportUnitSystem.Centimeters; break;
                case 2: units = ExportUnitSystem.Meters; break;
                case 3: units = ExportUnitSystem.Inches; break;
                case 4: units = ExportUnitSystem.Feet; break;
                default: units = ExportUnitSystem.Meters; break;
            }

            double tolerance = 0.001;
            double.TryParse(_toleranceTextBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out tolerance);

            Result = new ExportOptions
            {
                Mode = _exportModeDropDown.SelectedIndex == 0 ? ExportMode.General : ExportMode.FrameCAD,
                Units = units,
                Tolerance = tolerance,
                IfcSchema = _ifcSchemaDropDown.SelectedValue?.ToString() ?? "IFC2x3",
                Author = _authorTextBox.Text,
                Organization = _organizationTextBox.Text
            };
            Close();
        }
    }
}
