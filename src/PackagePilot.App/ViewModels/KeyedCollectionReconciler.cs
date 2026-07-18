using System.Collections.ObjectModel;

namespace PackagePilot.App.ViewModels;

internal interface IKeyedCollectionReconciler<TKey, TItem>
    where TKey : notnull
{
    void Reconcile(
        ObservableCollection<TItem> target,
        IEnumerable<TItem> replacement,
        Func<TItem, TKey> keySelector,
        Action<TItem, TItem>? update = null);
}

/// <summary>
/// Reconciles a collection without Clear/Add churn. Existing instances are retained so
/// ListView selection, scroll anchors, and asynchronously loaded icons survive state updates.
/// </summary>
internal sealed class KeyedCollectionReconciler<TKey, TItem>(
    IEqualityComparer<TKey>? comparer = null) : IKeyedCollectionReconciler<TKey, TItem>
    where TKey : notnull
{
    private readonly IEqualityComparer<TKey> _comparer = comparer ?? EqualityComparer<TKey>.Default;

    public void Reconcile(
        ObservableCollection<TItem> target,
        IEnumerable<TItem> replacement,
        Func<TItem, TKey> keySelector,
        Action<TItem, TItem>? update = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(keySelector);

        var desired = replacement.ToArray();
        var desiredKeys = desired.Select(keySelector).ToArray();
        if (desiredKeys.Distinct(_comparer).Count() != desiredKeys.Length)
        {
            throw new ArgumentException("Replacement keys must be unique.", nameof(replacement));
        }

        var desiredSet = desiredKeys.ToHashSet(_comparer);
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(keySelector(target[index])))
            {
                target.RemoveAt(index);
            }
        }

        for (var desiredIndex = 0; desiredIndex < desired.Length; desiredIndex++)
        {
            var replacementItem = desired[desiredIndex];
            var desiredKey = desiredKeys[desiredIndex];
            var existingIndex = IndexOf(target, desiredKey, keySelector);
            if (existingIndex < 0)
            {
                target.Insert(desiredIndex, replacementItem);
                continue;
            }

            if (existingIndex != desiredIndex)
            {
                target.Move(existingIndex, desiredIndex);
            }

            if (update is not null)
            {
                update(target[desiredIndex], replacementItem);
            }
        }
    }

    private int IndexOf(
        ObservableCollection<TItem> target,
        TKey key,
        Func<TItem, TKey> keySelector)
    {
        for (var index = 0; index < target.Count; index++)
        {
            if (_comparer.Equals(keySelector(target[index]), key))
            {
                return index;
            }
        }

        return -1;
    }
}
