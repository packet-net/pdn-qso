using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace PdnQso.Ui;

/// <summary>One thing an operator can pick, and what picking it means.</summary>
/// <param name="Label">What the list shows.</param>
/// <param name="Value">What goes in the field when it is picked.</param>
public readonly record struct Choice(string Label, string Value)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// Ask one question: a list of what this machine actually has, and a field for the answer that
/// is not in the list.
/// </summary>
/// <remarks>
/// The wizard is a run of these. Both halves matter: the list is what makes a first run
/// possible for somebody who does not know the device-string grammar, and the field is what
/// makes it possible for somebody whose radio is on another machine and was never going to be
/// in any list.
/// </remarks>
public static class ChoiceDialog
{
    /// <summary>Shows the question and returns the answer, or null if it was backed out of.</summary>
    /// <param name="app">The application instance.</param>
    /// <param name="title">The dialog title.</param>
    /// <param name="prompt">One line above the list saying what is being asked.</param>
    /// <param name="choices">What this machine has; may be empty.</param>
    /// <param name="initial">What the field starts with.</param>
    /// <param name="footer">An optional line under the field: a hint, or a grammar.</param>
    public static string? Show(
        IApplication app,
        string title,
        string prompt,
        IReadOnlyList<Choice> choices,
        string initial = "",
        string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(choices);

        using var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(80),
            Height = Dim.Percent(70),
        };

        dialog.Add(new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 1,
            Text = prompt,
        });

        // No list at all when there is nothing to list: an empty ListView would still take the
        // focus, and the operator's typing would go into a list that has no items to navigate
        // rather than into the field they are looking at.
        ListView? list = null;
        if (choices.Count > 0)
        {
            var labels = new ObservableCollection<string>(choices.Select(c => c.Label).ToList());
            list = new ListView
            {
                X = 1,
                Y = 2,
                Width = Dim.Fill(2),
                Height = Dim.Fill(5),
            };
            list.SetSource(labels);
            dialog.Add(list);
        }

        var field = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(2),
            Height = 1,
            Text = initial,
        };
        dialog.Add(field);

        if (footer is not null)
        {
            dialog.Add(new Label
            {
                X = 1,
                Y = Pos.AnchorEnd(2),
                Width = Dim.Fill(2),
                Height = 1,
                Text = footer,
            });
        }

        void TakeSelection()
        {
            if (list?.SelectedItem is int index && index >= 0 && index < choices.Count)
            {
                field.Text = choices[index].Value;
            }
        }

        if (list is not null)
        {
            list.ValueChanged += (_, _) => TakeSelection();
        }

        string? answer = null;
        var ok = new Button
        {
            Text = "_OK",
            IsDefault = true,
            X = Pos.AnchorEnd(20),
            Y = Pos.AnchorEnd(1),
        };
        var cancel = new Button
        {
            Text = "_Cancel",
            X = Pos.AnchorEnd(11),
            Y = Pos.AnchorEnd(1),
        };

        ok.Accepting += (_, e) =>
        {
            e.Handled = true;
            answer = field.Text.Trim();
            app.RequestStop(dialog);
        };

        cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            app.RequestStop(dialog);
        };

        if (list is not null)
        {
            list.Accepting += (_, e) =>
            {
                e.Handled = true;
                TakeSelection();
                answer = field.Text.Trim();
                app.RequestStop(dialog);
            };
        }

        // Enter in the field is Enter on OK: on the questions with nothing to pick from, the
        // field is the whole dialog and reaching for a button would be silly.
        field.Accepting += (_, e) =>
        {
            e.Handled = true;
            answer = field.Text.Trim();
            app.RequestStop(dialog);
        };

        dialog.Add(ok, cancel);

        // The list where there is one, so the arrow keys go where the eye does; the field where
        // there is not.
        dialog.Initialized += (_, _) => _ = (list ?? (View)field).SetFocus();

        app.Run(dialog);
        return string.IsNullOrWhiteSpace(answer) ? null : answer;
    }
}
