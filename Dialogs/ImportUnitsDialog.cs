using System;
using Eto.Drawing;
using Eto.Forms;
using SERhinoIFC.Import;
using Font = Eto.Drawing.Font;

namespace SERhinoIFC.Dialogs
{
    public class ImportUnitsDialog : Dialog<bool>
    {
        private readonly DropDown _ifcUnitOverride;
        private readonly IfcUnitInfo _detectedUnit;

        /// <summary>
        /// The final IFC unit info to use for import (detected or overridden).
        /// </summary>
        public IfcUnitInfo SelectedIfcUnit { get; private set; }

        private static readonly (string Name, double MetersPerUnit)[] UnitOptions =
        {
            ("Millimeters", 0.001),
            ("Centimeters", 0.01),
            ("Meters", 1.0),
            ("Inches", 0.0254),
            ("Feet", 0.3048),
            ("Yards", 0.9144),
        };

        public ImportUnitsDialog(IfcUnitInfo detectedUnit, Rhino.UnitSystem rhinoUnitSystem)
        {
            _detectedUnit = detectedUnit;
            SelectedIfcUnit = detectedUnit;

            Title = "IFC Import — Unit Check";
            Padding = new Padding(20);
            MinimumSize = new Size(420, 0);
            Resizable = false;

            // Detected IFC unit display
            var detectedLabel = new Label
            {
                Text = detectedUnit.DisplayName,
                Font = new Font(SystemFont.Bold, 12),
                TextColor = Colors.DarkGreen
            };

            // Rhino unit display
            var rhinoLabel = new Label
            {
                Text = rhinoUnitSystem.ToString(),
                Font = new Font(SystemFont.Bold, 12)
            };

            // Override dropdown
            _ifcUnitOverride = new DropDown();
            int selectedIndex = 0;
            for (int i = 0; i < UnitOptions.Length; i++)
            {
                _ifcUnitOverride.Items.Add(UnitOptions[i].Name);
                if (Math.Abs(UnitOptions[i].MetersPerUnit - detectedUnit.MetersPerUnit) < 1e-8)
                    selectedIndex = i;
            }
            _ifcUnitOverride.SelectedIndex = selectedIndex;

            // Buttons
            var importButton = new Button { Text = "Import" };
            importButton.Click += OnImportClick;

            var cancelButton = new Button { Text = "Cancel" };
            cancelButton.Click += (s, e) => { Result = false; Close(); };

            DefaultButton = importButton;
            AbortButton = cancelButton;

            // Layout
            Content = new StackLayout
            {
                Spacing = 15,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new TableLayout
                    {
                        Spacing = new Size(10, 10),
                        Rows =
                        {
                            new TableRow(
                                new Label { Text = "Detected IFC Units:", VerticalAlignment = VerticalAlignment.Center },
                                detectedLabel
                            ),
                            new TableRow(
                                new Label { Text = "Current Rhino Units:", VerticalAlignment = VerticalAlignment.Center },
                                rhinoLabel
                            ),
                            new TableRow(
                                new Label { Text = "Import As:", VerticalAlignment = VerticalAlignment.Center },
                                _ifcUnitOverride
                            ),
                        }
                    },
                    new Label
                    {
                        Text = "Change \"Import As\" only if the detected unit is wrong.",
                        TextColor = Colors.Gray,
                        Font = new Font(SystemFont.Default, 9)
                    },
                    new TableLayout
                    {
                        Spacing = new Size(5, 0),
                        Rows =
                        {
                            new TableRow(null, importButton, cancelButton)
                        }
                    }
                }
            };
        }

        private void OnImportClick(object sender, EventArgs e)
        {
            int idx = _ifcUnitOverride.SelectedIndex;
            var chosen = UnitOptions[idx];

            SelectedIfcUnit = new IfcUnitInfo
            {
                UnitName = chosen.Name,
                MetersPerUnit = chosen.MetersPerUnit
            };

            Result = true;
            Close();
        }
    }
}
