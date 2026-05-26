using System.Text.RegularExpressions;

namespace LTAI.Tools.GIS;

public enum SpatialIntent
{
    NEARBY,
    POI,
    ROUTING,
    TRIP,
    UNKNOWN
}

public sealed class SpatialIntentRouter
{
    public static SpatialIntent DetectIntent(string query)
    {
        var lower = query.ToLowerInvariant();

        if (Regex.IsMatch(lower, @"\b(near|nearby|closest|nearest|near me|around|within \d+|surrounding|附近|周围|最近的|旁边的)\b") ||
            Regex.IsMatch(lower, @"\b(count|how many|find|show|list).*(restaurant|cafe|hotel|shop|store|place|cafe|park|hospital|school|museum|cinema|bar|bank|atm|pharmacy|gas station)\b") ||
            Regex.IsMatch(lower, @"\b(\d+)\s*(meter|km|kilometer|mile|m)\b.*\b(within|radius|around)\b"))
            return SpatialIntent.NEARBY;

        if (Regex.IsMatch(lower, @"\b(route|direction|road|path|way|drive|walk|transit|bike|how to get|navigate|navigation|next step|after reaching|from.*to)\b") ||
            Regex.IsMatch(lower, @"\b(travel time|distance|duration|eta|arrive|depart|via)\b") ||
            Regex.IsMatch(lower, @"\b(怎么走|路线|导航|开车|步行|公交)\b"))
            return SpatialIntent.ROUTING;

        if (Regex.IsMatch(lower, @"\b(trip|itinerary|schedule|tour|visit.*then|plan.*day|multi.*stop|best order|sequence|feasibility|latest.*depart|latest.*visit|finish.*by|deadline)\b") ||
            Regex.IsMatch(lower, @"\b(行程|路线规划|先去|再去|最后去|顺序|时刻表)\b") ||
            lower.Split(',').Length >= 3)
            return SpatialIntent.TRIP;

        if (Regex.IsMatch(lower, @"\b(open|closed|hours|rating|review|phone|address|direction.*between|bearing|where is|what is|tell me about|compare|between|closest.*each|coordinate|定位|评价|营业|开门|几点|电话|地址)\b"))
            return SpatialIntent.POI;

        return SpatialIntent.UNKNOWN;
    }
}
