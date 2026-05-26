namespace LTAI.AI.Governors;

public sealed class GovernorSet
{
    public InputGovernor Input { get; }
    public ContextGovernor Context { get; }
    public RoutingGovernor Routing { get; }
    public OutputGovernor Output { get; }
    public SelfGovernor Self { get; }
    public SystemGuardian Guardian { get; }

    public GovernorSet(
        InputGovernor input,
        ContextGovernor context,
        RoutingGovernor routing,
        OutputGovernor output,
        SelfGovernor self,
        SystemGuardian guardian)
    {
        Input = input;
        Context = context;
        Routing = routing;
        Output = output;
        Self = self;
        Guardian = guardian;
    }
}
