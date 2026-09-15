using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace SwAgent.AddIn
{
    /// <summary>
    /// The task pane's UI: Panel/panel.html, embedded in this assembly.
    ///
    /// Two screens: key setup, and chat. Setup gets disproportionate care
    /// because it is the conversion-critical path - we are asking a mechanical
    /// engineer to go and create an Anthropic account before they have seen the
    /// tool do anything - and because the promise about where their data goes
    /// has to be stated plainly, in the UI, at the moment they hand over a
    /// credential.
    ///
    /// Served via NavigateToString, so there is no origin, no network fetch and
    /// nothing external to load. Preview it in a browser against a fake host
    /// with tools/preview-panel.ps1.
    /// </summary>
    internal static class PanelHtml
    {
        private const string ResourceName = "SwAgent.AddIn.Panel.panel.html";

        private static string _text;

        public static string Text => _text ?? (_text = Load());

        private static string Load()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException($"The panel page is missing from the add-in ({ResourceName}).");

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }
    }
}
