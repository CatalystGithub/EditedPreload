using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace EditedPreload
{
    public class PluginSettings : ISettings
    {
        public ToggleNode Enable { get; set; }
        public ToggleNode Test1;

        public PluginSettings()
        {
            Test1 = new ToggleNode(true);
        }
    }
}
