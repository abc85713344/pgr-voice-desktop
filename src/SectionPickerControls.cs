using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;

namespace PgrVoice;

public partial class MainWindow
{
    SectionPickerWindow? sectionPicker;
    Action<SectionPickerWindow>? sectionPickerTest;
    readonly Dictionary<ComboBox, Button> sectionPickerButtons = new();
    IEnumerable<Section> PickerSections(ComboBox model) => ReferenceEquals(model, listeningSections)
        ? listeningSession?.Chapter.Sections ?? Enumerable.Empty<Section>() : model.Items.OfType<Section>();
    string? PickerSectionId(ComboBox model) => (model.SelectedItem as Section)?.Id ?? (model.SelectedItem as ListeningEntry)?.Id;
    string SectionDisplayTitle(string? id, Pack? pack = null) => SectionDisplay.Title((pack ?? engine?.Pack)?.Chapters.SelectMany(c => c.Sections) ?? Enumerable.Empty<Section>(), id ?? "");

    Button SectionPickerControl(ComboBox model)
    {
        model.Visibility = Visibility.Collapsed;
        var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6) };
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis }; button.Content = text;
        void Refresh()
        {
            string id = PickerSectionId(model) ?? "";
            text.Text = id.Length == 0 ? "选择小节" : SectionDisplay.Title(PickerSections(model), id);
            AutomationProperties.SetName(button, "选择小节：" + text.Text);
        }
        model.SelectionChanged += (_, _) => Refresh();
        button.SetBinding(IsEnabledProperty, new Binding("HasItems") { Source = model });
        button.Click += (_, _) => OpenSectionPicker(model); sectionPickerButtons[model] = button; Refresh();
        return button;
    }
    void OpenSectionPicker(ComboBox model)
    {
        if (sectionPicker != null) { sectionPicker.Activate(); return; }
        var pack = ReferenceEquals(model, listeningSections) ? listeningSession?.Pack : engine?.Pack;
        var sections = PickerSections(model).ToArray(); if (pack == null || sections.Length == 0) return;
        var picker = new SectionPickerWindow(pack, sections, sections.FirstOrDefault(s => s.Id == PickerSectionId(model))) { Owner = this };
        sectionPicker = picker;
        if (testUi && sectionPickerTest != null) picker.Loaded += (_, _) => sectionPickerTest(picker);
        try
        {
            if (picker.ShowDialog() != true || picker.Result is not { } chosen) return;
            var currentPack = ReferenceEquals(model, listeningSections) ? listeningSession?.Pack : engine?.Pack;
            if (!ReferenceEquals(pack, currentPack)) return;
            object? item = ReferenceEquals(model, listeningSections)
                ? model.Items.OfType<ListeningEntry>().FirstOrDefault(s => s.Id == chosen.Id)
                : model.Items.OfType<Section>().FirstOrDefault(s => s.Id == chosen.Id);
            if (item != null) model.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, item);
        }
        finally { sectionPicker = null; ResetGamepadContext(); }
    }
}
