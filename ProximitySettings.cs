using System.Text.Json.Serialization;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Vector2 = System.Numerics.Vector2;

namespace ProximityAlert;

public class ProximitySettings : ISettings
{
    [JsonIgnore]
    public ButtonNode Reload { get; set; } = new ButtonNode();

    [JsonIgnore]
    public ButtonNode CopyDefaultConfigsToConfigFolder { get; set; } = new ButtonNode();

    public RangeNode<float> Scale { get; set; } = new RangeNode<float>(1, (float)0.1, 10);
    public RangeNode<Vector2> AlertPositionOffset { get; set; } = new RangeNode<Vector2>(Vector2.Zero, Vector2.One * -3840, Vector2.One * 2560);
    public ToggleNode EnableMultithreading { get; set; } = new ToggleNode(true);

    [Menu(null, "By default this covers things such as corrupting blood")]
    public ToggleNode ShowBeastAlerts { get; set; } = new ToggleNode(false);

    public ToggleNode ShowModAlerts { get; set; } = new ToggleNode(false);
    
    public ToggleNode ShowPathAlerts { get; set; } = new ToggleNode(true);

    public ToggleNode DrawALineToRealSirus { get; set; } = new ToggleNode(true);

    [Menu(null, "Sounds can be found and go in Hud\\Plugins\\Compiled\\ProximityAlert\\")]
    public ToggleNode PlaySoundsForAlerts { get; set; } = new ToggleNode(true);

    public ToggleNode Enable { get; set; } = new ToggleNode(true);
}