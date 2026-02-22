using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ChrisDowd.NINA.MoveFocusAfterTempChange.MoveFocusAfterTempChangeTestCategory {
 
    [Export(typeof(ResourceDictionary))]
    public partial class PluginItemTemplate : ResourceDictionary {

        private static readonly Regex _regex = new Regex(@"^\d*(\.\d?)?$");

        public PluginItemTemplate() {
            InitializeComponent();
        }

        private void NumericOnly(object sender, TextCompositionEventArgs e) {
            var tb = sender as System.Windows.Controls.TextBox;

            string proposed = tb.Text.Insert(tb.SelectionStart, e.Text);

            e.Handled = !_regex.IsMatch(proposed);
        }
    }
}