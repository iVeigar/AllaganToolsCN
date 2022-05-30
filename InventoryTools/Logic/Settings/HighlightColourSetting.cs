using System.Numerics;
using InventoryTools.Logic.Settings.Abstract;

namespace InventoryTools.Logic.Settings
{
    public class HighlightColourSetting : ColorSetting
    {
        public override Vector4 DefaultValue { get; set; } = new(0.007f, 0.008f,
            0.007f, 0.212f);
        public override Vector4 CurrentValue(InventoryToolsConfiguration configuration)
        {
            return configuration.HighlightColor;
        }

        public override void UpdateFilterConfiguration(InventoryToolsConfiguration configuration, Vector4 newValue)
        {
            configuration.HighlightColor = newValue;
        }

        public override string Key { get; set; } = "HighlightColour";
        public override string Name { get; set; } = "Highlight Colour";
        public override string HelpText { get; set; } = "The color to set the highlighted items to.";
        public override SettingCategory SettingCategory { get; set; } = SettingCategory.Visuals;
        public override SettingSubCategory SettingSubCategory { get; } = SettingSubCategory.Highlighting;
    }
}