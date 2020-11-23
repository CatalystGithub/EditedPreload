using ExileCore;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace EditedPreload
{
    public class EditedPreload : BaseSettingsPlugin<TestSettings>
    {
        public new string Name => "Plugin Adi";
    }

    public class TestSettings : ISettings
    {
        public ToggleNode Enable { get; set; }
        public ToggleNode Test1;

        public TestSettings()
        {
            Test1 = new ToggleNode(true);
        }
    }
}
