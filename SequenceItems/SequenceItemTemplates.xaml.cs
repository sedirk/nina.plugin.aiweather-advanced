using System.ComponentModel.Composition;
using System.Windows;

namespace SkySentinel.SequenceItems {
    [Export(typeof(ResourceDictionary))]
    public partial class SequenceItemTemplates : ResourceDictionary {
        public SequenceItemTemplates() {
            InitializeComponent();
        }
    }
}
