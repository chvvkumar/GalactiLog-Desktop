using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.Core.Help;

namespace GalactiLog.App.Controls;

/// <summary>
/// Spec 12.12's help glyph: a ring with an <c>i</c> in it beside a page or section heading, which
/// opens that heading's paragraph in a <see cref="Flyout"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>.axaml</c> beside this file, for the same reason <see cref="NightStrip"/> has
/// none: there are no templated children for a <c>ControlTemplate</c> to hold. The whole
/// appearance, the two drawn <c>Path</c> children and the flyout, is one style in
/// <c>Theme/Controls.axaml</c>, which is the only file <c>ControlStyleScanTest</c> lets a button
/// style live in.
/// </para>
/// <para>
/// One property, the topic id, and two read-only projections of it. <see cref="Topic"/> is a
/// styled property so markup can set it as an attribute and a selector can read it;
/// <see cref="TopicTitle"/> and <see cref="TopicParagraph"/> are direct properties, so the flyout's
/// two bindings re-read when the topic changes.
/// </para>
/// <para>
/// A topic that is not in <see cref="HelpTopics"/> throws from <c>HelpTopics.Get</c> and is not
/// swallowed. <c>HelpPlacementCensusTest</c> is what keeps a build from reaching that throw, and
/// the throw is what makes the census load bearing.
/// </para>
/// <para>
/// There is no pointer handler of any kind here, and there must not be one: hover does nothing is
/// spec 12.12's ruling rather than an omission. A reader moving the pointer across a page of
/// headings would otherwise walk through a corridor of opening panels.
/// </para>
/// </remarks>
public sealed class HelpButton : Button
{
    /// <summary>The accessible name's fixed head, spec 12.12. The topic's title follows it, so a
    /// screen reader tells two glyphs on one page apart.</summary>
    public const string AccessibleNamePrefix = "About this section ";

    /// <summary>The topic id this glyph names, matched ordinally against
    /// <see cref="HelpTopics"/>.</summary>
    public static readonly StyledProperty<string> TopicProperty =
        AvaloniaProperty.Register<HelpButton, string>(nameof(Topic), defaultValue: "");

    /// <summary>The topic's title, resolved from the table.</summary>
    public static readonly DirectProperty<HelpButton, string> TopicTitleProperty =
        AvaloniaProperty.RegisterDirect<HelpButton, string>(
            nameof(TopicTitle), button => button.TopicTitle);

    /// <summary>The topic's paragraph, resolved from the table.</summary>
    public static readonly DirectProperty<HelpButton, string> TopicParagraphProperty =
        AvaloniaProperty.RegisterDirect<HelpButton, string>(
            nameof(TopicParagraph), button => button.TopicParagraph);

    /// <summary>A control drawn above the topic in this glyph's flyout, or null for the topic alone.</summary>
    public static readonly StyledProperty<Control?> FlyoutHeaderProperty =
        AvaloniaProperty.Register<HelpButton, Control?>(nameof(FlyoutHeader));

    /// <summary>The style class the glyph's own style in <c>Theme/Controls.axaml</c> selects on.
    /// The control adds it to itself, so no call site can forget it and no view needs to repeat
    /// it.</summary>
    public const string GlyphClass = "help";

    /// <summary>The flat glyph-button shape this control composes with: padding 6,0, no border,
    /// tertiary ink, primary ink on hover.</summary>
    /// <remarks>Added alongside <see cref="GlyphClass"/> rather than restated (Task 2A review P3).
    /// <c>Button.help</c> had its own copy of those four setters, which is the same value written
    /// in two places for the same reason; it now carries only what a help glyph adds to the
    /// chevron, and the two styles cannot drift.</remarks>
    public const string ShapeClass = "chevron";

    /// <summary>The key of the flyout body's <c>DataTemplate</c> in
    /// <c>Theme/Controls.axaml</c>.</summary>
    public const string FlyoutTemplateKey = "HelpTopicFlyoutTemplate";

    private string _topicTitle = "";
    private string _topicParagraph = "";
    private Control? _builtHeader;

    /// <summary>Adds the two style classes the shared vocabulary selects on.</summary>
    public HelpButton()
    {
        Classes.Add(ShapeClass);
        Classes.Add(GlyphClass);
    }

    /// <summary>
    /// <c>Button</c>, not <c>HelpButton</c>: a control deriving from a templated control finds no
    /// <c>ControlTheme</c> registered under its own type, so without this override the glyph has
    /// no template and renders nothing at all. It is also why the shared style selects on
    /// <c>Button.help</c> rather than on the type: a type selector matches on the style key, so a
    /// <c>controls|HelpButton</c> selector could never match once this is set.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The topic id this glyph names.</summary>
    public string Topic
    {
        get => GetValue(TopicProperty);
        set => SetValue(TopicProperty, value);
    }

    /// <summary>Drawn above the topic in the flyout; the flyout is rebuilt when it changes.</summary>
    public Control? FlyoutHeader
    {
        get => GetValue(FlyoutHeaderProperty);
        set => SetValue(FlyoutHeaderProperty, value);
    }

    /// <summary>The topic's title, empty while no topic is set.</summary>
    public string TopicTitle => _topicTitle;

    /// <summary>The topic's paragraph, empty while no topic is set.</summary>
    public string TopicParagraph => _topicParagraph;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Not in the constructor and not in the property-changed handler alone: the flyout's body
        // is a keyed template in Theme/Controls.axaml, and a resource lookup needs the control to
        // be in a tree that reaches the application's styles.
        //
        // The test is "the flyout does not match the topic", not "there is no flyout" (Task 2A
        // review P3). A Topic that changed while the control was detached left the previous
        // topic's flyout in place, because the property-changed handler's own BuildFlyout could
        // not reach the resource and the attach handler then found a flyout and left it alone.
        // Unreachable in the shipped markup, where the one bound site is an ItemsControl that
        // rebuilds its items, and cheap to close: rebuilding a flyout whose content is already the
        // current topic is what is skipped, so a re-attach still does not replace one on screen.
        if (Flyout is not Flyout flyout || !ReferenceEquals(flyout.Content, CurrentTopic()) || !ReferenceEquals(_builtHeader, FlyoutHeader))
        {
            BuildFlyout();
        }
    }

    // The HelpTopics entry this glyph's flyout should be carrying, or null when it has no topic.
    // HelpTopics.Get returns the one table entry per id, so reference equality against the
    // flyout's content is the whole comparison.
    private object? CurrentTopic()
        => string.IsNullOrEmpty(Topic) ? null : HelpTopics.Get(Topic);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FlyoutHeaderProperty)
        {
            BuildFlyout();
            return;
        }

        if (change.Property != TopicProperty)
        {
            return;
        }

        // The topic arrives from markup after construction, which is why the accessible name is
        // set here rather than in a constructor.
        var id = Topic;
        var title = "";
        var paragraph = "";
        if (!string.IsNullOrEmpty(id))
        {
            var topic = HelpTopics.Get(id);
            title = topic.Title;
            paragraph = topic.Paragraph;
        }

        SetAndRaise(TopicTitleProperty, ref _topicTitle, title);
        SetAndRaise(TopicParagraphProperty, ref _topicParagraph, paragraph);
        AutomationProperties.SetName(this, AccessibleNamePrefix + title);
        BuildFlyout();
    }

    /// <summary>
    /// Builds this glyph's own flyout over its own topic.
    /// </summary>
    /// <remarks>
    /// One flyout per button, built here rather than in the style, for two reasons that are both
    /// Avalonia's rather than this application's. A <c>Setter</c> whose value is a
    /// <c>Template</c> builds a <c>Control</c>, and a <c>Flyout</c> is not one; and a
    /// <c>Flyout</c> written straight into a setter would be a single object shared by every glyph
    /// in the application, retargeted on each open. The body, its width and every token it uses
    /// stay in <c>Theme/Controls.axaml</c> as a keyed <c>DataTemplate</c> over
    /// <see cref="HelpTopic"/>, which is what this reads.
    /// </remarks>
    private void BuildFlyout()
    {
        if (string.IsNullOrEmpty(Topic))
        {
            Flyout = null;
            return;
        }

        if (!this.TryFindResource(FlyoutTemplateKey, out var resource))
        {
            // Before the control is in a tree the styles are not reachable; the attach handler
            // runs this again. A genuinely missing template is caught by the throw below.
            return;
        }

        if (resource is not IDataTemplate template)
        {
            throw new KeyNotFoundException(
                $"The resource {FlyoutTemplateKey} in Theme/Controls.axaml is not a DataTemplate, "
                + "so the help flyout has no body.");
        }

        var header = FlyoutHeader;
        var flyout = new Flyout
        {
            Content = HelpTopics.Get(Topic),
            ContentTemplate = header is null ? template : WithHeader(header, template),
            Placement = PlacementMode.BottomEdgeAlignedLeft,

            // Spec 12.12: the open flyout takes focus, so the paragraph is reachable and
            // dismissable from the keyboard alone. Stated rather than left to the default, and
            // never TransientWithDismissOnPointerMoveAway, which would make the pointer matter.
            ShowMode = FlyoutShowMode.Standard,
        };

        // ShowMode alone does not deliver that clause, which is what measuring it showed (Task 2A
        // review P3): the mode governs light dismissal, and Avalonia's popup moves the keyboard
        // nowhere by itself. The body is declared Focusable in the template so there is something
        // to move it to, and this is what moves it.
        flyout.Opened += OnFlyoutOpened;
        Flyout = flyout;
        _builtHeader = header;
    }

    // The header is one control, so a body built for an earlier open gives it up first.
    private static IDataTemplate WithHeader(Control header, IDataTemplate template)
        => new FuncDataTemplate<HelpTopic>((topic, _) =>
        {
            switch (header.Parent)
            {
                case Panel panel:
                    panel.Children.Remove(header);
                    break;
                case ContentControl { Content: var held } holder when ReferenceEquals(held, header):
                    holder.Content = null;
                    break;
                case Avalonia.Controls.Presenters.ContentPresenter { Content: var held } presenter when ReferenceEquals(held, header):
                    presenter.Content = null;
                    break;
                case Decorator { Child: var held } decorator when ReferenceEquals(held, header):
                    decorator.Child = null;
                    break;
            }

            var body = new StackPanel
            {
                Spacing = 8,
                Children = { header, new ContentControl { Content = topic, ContentTemplate = template } },
            };

            // The presenter scrolls sideways by default, which would measure the header at its full
            // width and clip it; with that off the header wraps inside the flyout.
            body.AttachedToVisualTree += (_, _) =>
            {
                if (body.FindAncestorOfType<FlyoutPresenter>() is { } presenter)
                {
                    ScrollViewer.SetHorizontalScrollBarVisibility(presenter, ScrollBarVisibility.Disabled);
                }
            };
            return body;
        });

    private static void OnFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is not IPopupHostProvider provider)
        {
            return;
        }

        // Posted rather than called here: the host is raised as open before its content has been
        // through a layout pass, so a focus call on this turn has nothing realised to land on, and
        // the click that opened the flyout puts the focus back on the glyph after this returns.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (provider.PopupHost is Visual host)
                {
                    host.GetVisualDescendants()
                        .OfType<InputElement>()
                        .FirstOrDefault(element => element.Focusable)
                        ?.Focus(NavigationMethod.Unspecified);
                }
            },
            DispatcherPriority.Input);
    }
}
