using System;
using Eto.Drawing;
using Eto.Forms;
using SERhinoIFC.Export;

namespace SERhinoIFC.Dialogs
{
    public class ExportOptionsDialog : Dialog<ExportOptions>
    {
        private readonly DropDown _exportModeDropDown;
        private readonly DropDown _ifcSchemaDropDown;
        private readonly TextBox _authorTextBox;
        private readonly TextBox _organizationTextBox;
        private readonly TextBox _framePrefixTextBox;
        private readonly TableRow _framePrefixRow;

        public ExportOptionsDialog()
        {
            Title = "IFC Export Options";
            Padding = new Padding(15);
            MinimumSize = new Size(400, 0);
            Resizable = false;

            // Export Mode
            _exportModeDropDown = new DropDown();
            _exportModeDropDown.Items.Add("General IFC");
            _exportModeDropDown.Items.Add("FrameCAD / Constructobot");
            _exportModeDropDown.SelectedIndex = 0;

            // IFC Schema
            _ifcSchemaDropDown = new DropDown();
            _ifcSchemaDropDown.Items.Add("IFC2x3");
            _ifcSchemaDropDown.Items.Add("IFC4");
            _ifcSchemaDropDown.SelectedIndex = 0;

            // Author
            _authorTextBox = new TextBox { Text = Environment.UserName };

            // Organization
            _organizationTextBox = new TextBox();

            // Frame Name Prefix (conditional)
            _framePrefixTextBox = new TextBox { PlaceholderText = "e.g. F1" };
            _framePrefixRow = new TableRow(
                new Label { Text = "Frame Name Prefix", VerticalAlignment = VerticalAlignment.Center },
                _framePrefixTextBox
            );
            _framePrefixRow.ScaleHeight = false;

            // Toggle frame prefix row visibility based on export mode
            _exportModeDropDown.SelectedIndexChanged += (s, e) => UpdateFramePrefixVisibility();

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
                    _framePrefixRow
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

            // Initial visibility
            UpdateFramePrefixVisibility();
        }

        private void UpdateFramePrefixVisibility()
        {
            bool isFrameCAD = _exportModeDropDown.SelectedIndex == 1;
            _framePrefixTextBox.Visible = isFrameCAD;

            // Find the label in the row and toggle it too
            if (_framePrefixRow.Cells.Count > 0 && _framePrefixRow.Cells[0].Control != null)
                _framePrefixRow.Cells[0].Control.Visible = isFrameCAD;
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            Result = new ExportOptions
            {
                Mode = _exportModeDropDown.SelectedIndex == 0 ? ExportMode.General : ExportMode.FrameCAD,
                IfcSchema = _ifcSchemaDropDown.SelectedValue?.ToString() ?? "IFC2x3",
                Author = _authorTextBox.Text,
                Organization = _organizationTextBox.Text,
                FrameNamePrefix = _framePrefixTextBox.Text
            };
            Close();
        }
    }
}
