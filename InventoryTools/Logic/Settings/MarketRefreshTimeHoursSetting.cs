using InventoryTools.Logic.Settings.Abstract;

namespace InventoryTools.Logic.Settings
{
    public class MarketRefreshTimeHoursSetting : IntegerSetting
    {
        public override int DefaultValue { get; set; } = 24;
        public override int CurrentValue(InventoryToolsConfiguration configuration)
        {
            return configuration.MarketRefreshTimeHours;
        }

        public override void UpdateFilterConfiguration(InventoryToolsConfiguration configuration, int newValue)
        {
            configuration.MarketRefreshTimeHours = newValue;
        }

        public override string Key { get; set; } = "MarketRefreshTime";
        public override string Name { get; set; } = "Keep market prices for X hours";
        public override string HelpText { get; set; } = "How long should we store the market prices for before refreshing from universalis?";
        public override SettingCategory SettingCategory { get; set; } = SettingCategory.MarketBoard;
        public override SettingSubCategory SettingSubCategory { get; } = SettingSubCategory.Market;

    }
}