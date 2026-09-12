using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Core.Session;

namespace SwAgent.Core.Properties
{
    /// <summary>Which tier a custom property lives on.</summary>
    public enum PropertyTier
    {
        /// <summary>Decide from where the property already exists.</summary>
        Auto,

        /// <summary>File-level: Extension.CustomPropertyManager("").</summary>
        File,

        /// <summary>Configuration-specific: the active configuration's manager.</summary>
        Configuration
    }

    /// <summary>One property as it actually resolves.</summary>
    public sealed class PropertyValue
    {
        public string Name { get; set; }

        /// <summary>What is stored, which may be an expression such as "$PRP:SW-Mass".</summary>
        public string RawValue { get; set; }

        /// <summary>What the user sees in a BOM or title block.</summary>
        public string ResolvedValue { get; set; }

        public PropertyTier FoundOn { get; set; }
        public bool Exists => RawValue != null;
    }

    /// <summary>
    /// Custom properties, read the way SOLIDWORKS itself resolves them.
    ///
    /// Properties are two-tier and the lookup cascades: configuration-specific
    /// properties live on the active configuration's manager, file-level ones
    /// on Extension.CustomPropertyManager(""). The configuration tier wins, and
    /// the file tier is the fallback when it comes back empty.
    ///
    /// Getting that order wrong produces values that disagree with what the
    /// user sees in their own BOM table - which is worse than failing, because
    /// it looks like it worked.
    /// </summary>
    public static class CustomProperties
    {
        /// <summary>
        /// Read a property, cascading configuration tier then file tier.
        /// </summary>
        public static PropertyValue Read(SwSession session, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A property name is required.", nameof(name));

            var doc = session.RequireModel();

            // Configuration tier first - that is the one that wins.
            var configManager = TryGetConfigurationManager(doc);
            if (configManager != null)
            {
                var fromConfig = ReadFrom(configManager, name, PropertyTier.Configuration);
                if (fromConfig.Exists && !string.IsNullOrEmpty(fromConfig.ResolvedValue))
                    return fromConfig;
            }

            var fileManager = doc.Extension.get_CustomPropertyManager("");
            return ReadFrom(fileManager, name, PropertyTier.File);
        }

        /// <summary>Every property on both tiers, configuration values winning.</summary>
        public static List<PropertyValue> ReadAll(SwSession session)
        {
            var doc = session.RequireModel();
            var seen = new Dictionary<string, PropertyValue>(StringComparer.OrdinalIgnoreCase);

            // File tier first so configuration values overwrite them.
            CollectInto(seen, doc.Extension.get_CustomPropertyManager(""), PropertyTier.File);

            var configManager = TryGetConfigurationManager(doc);
            if (configManager != null)
                CollectInto(seen, configManager, PropertyTier.Configuration);

            return new List<PropertyValue>(seen.Values);
        }

        /// <summary>
        /// Write a property.
        ///
        /// With <see cref="PropertyTier.Auto"/>, an existing property is
        /// updated on whichever tier it already lives, and a new one goes to
        /// the file tier. That matters: writing a part number to the wrong tier
        /// leaves the title block blank while reporting success, because the
        /// sheet is bound to the tier the user's template uses.
        /// </summary>
        public static PropertyTier Write(SwSession session, string name, string value, PropertyTier tier = PropertyTier.Auto)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A property name is required.", nameof(name));

            var doc = session.RequireModel();
            PropertyTier target = tier;

            if (tier == PropertyTier.Auto)
            {
                var existing = Read(session, name);
                target = existing.Exists ? existing.FoundOn : PropertyTier.File;
            }

            ICustomPropertyManager manager = target == PropertyTier.Configuration
                ? TryGetConfigurationManager(doc)
                : doc.Extension.get_CustomPropertyManager("");

            if (manager == null)
            {
                // No active configuration to write to; the file tier always exists.
                manager = doc.Extension.get_CustomPropertyManager("");
                target = PropertyTier.File;
            }

            // Add3 with overwrite handles both create and update. Set2 alone
            // silently does nothing when the property does not yet exist.
            int result = manager.Add3(
                name,
                (int)swCustomInfoType_e.swCustomInfoText,
                value ?? string.Empty,
                (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);

            if (result != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged
                && result != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_MismatchAgainstExistingType)
            {
                throw new InvalidOperationException(
                    $"SOLIDWORKS refused to write the property '{name}' (result code {result}).");
            }

            return target;
        }

        private static PropertyValue ReadFrom(ICustomPropertyManager manager, string name, PropertyTier tier)
        {
            var value = new PropertyValue { Name = name, FoundOn = tier };
            if (manager == null) return value;

            string raw, resolved;
            bool wasResolved;

            // Get5 gives the stored text and the resolved text separately. A
            // title block shows the resolved one, so both are worth reporting:
            // "$PRP:SW-Mass" and "142.3 g" are very different answers to
            // "what is this property".
            manager.Get5(name, false, out raw, out resolved, out wasResolved);

            if (string.IsNullOrEmpty(raw) && string.IsNullOrEmpty(resolved))
                return value;

            value.RawValue = raw;
            value.ResolvedValue = string.IsNullOrEmpty(resolved) ? raw : resolved;
            return value;
        }

        private static void CollectInto(
            Dictionary<string, PropertyValue> into, ICustomPropertyManager manager, PropertyTier tier)
        {
            if (manager == null) return;

            var names = manager.GetNames() as string[];
            if (names == null) return;

            foreach (string name in names)
            {
                var value = ReadFrom(manager, name, tier);
                if (value.Exists) into[name] = value;
            }
        }

        private static ICustomPropertyManager TryGetConfigurationManager(IModelDoc2 doc)
        {
            try
            {
                var configManager = doc.ConfigurationManager;
                var active = configManager?.ActiveConfiguration;
                return active?.CustomPropertyManager;
            }
            catch
            {
                return null;
            }
        }
    }
}
