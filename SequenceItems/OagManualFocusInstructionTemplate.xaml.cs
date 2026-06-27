using System.ComponentModel.Composition;
using System.Windows;

namespace OagFocusAssist.SequenceItems;

[Export(typeof(ResourceDictionary))]
public partial class OagManualFocusInstructionTemplate : ResourceDictionary
{
    public OagManualFocusInstructionTemplate()
    {
        InitializeComponent();
    }
}