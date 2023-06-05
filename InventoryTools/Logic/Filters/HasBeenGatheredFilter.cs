using CriticalCommonLib.Models;
using CriticalCommonLib.Sheets;
using InventoryTools.Logic.Filters.Abstract;

namespace InventoryTools.Logic.Filters;

public class HasBeenGatheredFilter : BooleanFilter
{
    public override string Key { get; set; } = "HasBeenGathered";
    public override string Name { get; set; } = "Has been gathered before?";
    public override string HelpText { get; set; } = "Has this gathering item been gathered at least once by the currently logged in character? This only supports mining and botany at present.";
    public override FilterCategory FilterCategory { get; set; } = FilterCategory.Gathering;

    public override FilterType AvailableIn { get; set; } =
        FilterType.SearchFilter | FilterType.SortingFilter | FilterType.GameItemFilter;
    public override bool? FilterItem(FilterConfiguration configuration, InventoryItem item)
    {
        return FilterItem(configuration, item.Item);
    }

    public override bool? FilterItem(FilterConfiguration configuration, ItemEx item)
    {
        var currentValue = CurrentValue(configuration);
        if (currentValue == null) return true;

        var isItemGathered = PluginService.GameInterface.IsItemGathered(item.RowId);
        if (isItemGathered == null)
        {
            return null;
        }
        if(currentValue.Value && isItemGathered.Value)
        {
            return true;
        }
                
        return !currentValue.Value && !isItemGathered.Value;
    }
}