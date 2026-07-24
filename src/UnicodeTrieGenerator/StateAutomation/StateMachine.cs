// Copyright (c) Six Labors.
// Licensed under the Six Labors Split License.

using SixLabors.Fonts;

namespace UnicodeTrieGenerator.StateAutomation;

internal class StateMachine
{
    private const int InitialState = 1;
    private const int FailState = 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateMachine"/> class.
    /// </summary>
    /// <param name="stateTable">The state table.</param>
    /// <param name="accepting">The accepting states.</param>
    /// <param name="tags">The tags.</param>
    public StateMachine(int[][] stateTable, bool[] accepting, string[][] tags)
    {
        this.StateTable = stateTable;
        this.Accepting = accepting;
        this.Tags = tags;
    }

    /// <summary>
    /// Gets the state table.
    /// </summary>
    public int[][] StateTable { get; }

    /// <summary>
    /// Gets the accepting states.
    /// </summary>
    public bool[] Accepting { get; }

    /// <summary>
    /// Gets the tags.
    /// </summary>
    public string[][] Tags { get; }

    /// <summary>
    /// Returns an iterable object that yields pattern matches over the input sequence.
    /// </summary>
    /// <param name="input">The input sequence.</param>
    /// <returns>The <see cref="IEnumerable{StateMatch}"/>.</returns>
    public IEnumerable<StateMatch> Match(ReadOnlySpan<int> input)
    {
        List<StateMatch> matches = new(input.Length);

        MatchEnumerator enumerator = this.EnumerateMatches(input);
        while (enumerator.MoveNext())
        {
            matches.Add(new StateMatch()
            {
                StartIndex = enumerator.StartIndex,
                EndIndex = enumerator.EndIndex,
                Tags = this.Tags[enumerator.TagState]
            });
        }

        return matches;
    }

    /// <summary>
    /// Returns an allocation-free enumerator over the pattern matches in the input
    /// sequence. Each match exposes the accepting run bounds and the state whose tag
    /// row identifies the match, so callers translate tags through precomputed
    /// per-state tables instead of touching the tag strings.
    /// </summary>
    /// <param name="input">The input sequence.</param>
    /// <returns>The <see cref="MatchEnumerator"/>.</returns>
    public MatchEnumerator EnumerateMatches(ReadOnlySpan<int> input) => new(this, input);

    /// <summary>
    /// For each match over the input sequence, action functions matching
    /// the tag definitions in the input pattern are called with the startIndex,
    /// length, and the sequence to be sliced.
    /// </summary>
    /// <param name="input">The input sequence.</param>
    /// <param name="actions">The collection of actions.</param>
    public void Apply(int[] input, Dictionary<string, Action<int, int, ArraySlice<int>>> actions)
    {
        foreach (StateMatch match in this.Match(input))
        {
            foreach (string tag in match.Tags)
            {
                if (actions.TryGetValue(tag, out Action<int, int, ArraySlice<int>>? action))
                {
                    action(match.StartIndex, match.EndIndex, new ArraySlice<int>(input, match.StartIndex, match.EndIndex + 1 - match.StartIndex));
                }
            }
        }
    }

    /// <summary>
    /// Enumerates pattern matches over an input sequence without allocating: the
    /// traversal state lives in the struct and each match is exposed through the
    /// bounds and tag-state properties rather than a match object.
    /// </summary>
    public ref struct MatchEnumerator
    {
        private readonly StateMachine machine;
        private readonly ReadOnlySpan<int> input;
        private int position;
        private int state;

        /// <summary>
        /// The start index of the run in progress, or -1 when no run is open.
        /// </summary>
        private int startRun;

        /// <summary>
        /// The index of the most recent accepting symbol, or -1 when none has been
        /// seen. Emission requires it to fall inside the open run.
        /// </summary>
        private int lastAccepting;

        /// <summary>
        /// Initializes a new instance of the <see cref="MatchEnumerator"/> struct
        /// positioned before the first match.
        /// </summary>
        /// <param name="machine">The state machine to run.</param>
        /// <param name="input">The input sequence.</param>
        public MatchEnumerator(StateMachine machine, ReadOnlySpan<int> input)
        {
            this.machine = machine;
            this.input = input;
            this.position = 0;
            this.state = InitialState;
            this.startRun = -1;
            this.lastAccepting = -1;
        }

        /// <summary>
        /// Gets the start index of the current match.
        /// </summary>
        public int StartIndex { get; private set; }

        /// <summary>
        /// Gets the inclusive end index of the current match.
        /// </summary>
        public int EndIndex { get; private set; }

        /// <summary>
        /// Gets the index of the state whose tag row identifies the current match.
        /// </summary>
        public int TagState { get; private set; }

        /// <summary>
        /// Advances to the next match.
        /// </summary>
        /// <returns><see langword="true"/> if a match was found.</returns>
        public bool MoveNext()
        {
            int[][] stateTable = this.machine.StateTable;
            bool[] accepting = this.machine.Accepting;

            while (this.position < this.input.Length)
            {
                int c = this.input[this.position];

                int lastState = this.state;
                this.state = stateTable[this.state][c];

                bool emit = false;
                if (this.state == FailState)
                {
                    // Yield the last match if any.
                    if (this.startRun != -1 && this.lastAccepting >= this.startRun)
                    {
                        this.StartIndex = this.startRun;
                        this.EndIndex = this.lastAccepting;
                        this.TagState = lastState;
                        emit = true;
                    }

                    // Reset the state as if we started over from the initial state.
                    this.state = stateTable[InitialState][c];
                    this.startRun = -1;
                }

                // Start a run if not in the failure state.
                if (this.state != FailState && this.startRun == -1)
                {
                    this.startRun = this.position;
                }

                // If accepting, mark the potential match end.
                if (accepting[this.state])
                {
                    this.lastAccepting = this.position;
                }

                // Reset the state to the initial state if we get into the failure state.
                if (this.state == FailState)
                {
                    this.state = InitialState;
                }

                this.position++;
                if (emit)
                {
                    return true;
                }
            }

            // Yield the last match if any.
            if (this.startRun != -1 && this.lastAccepting >= this.startRun)
            {
                this.StartIndex = this.startRun;
                this.EndIndex = this.lastAccepting;
                this.TagState = this.state;
                this.startRun = -1;
                return true;
            }

            return false;
        }
    }
}

internal class StateMatch : IEquatable<StateMatch?>
{
    public int StartIndex { get; set; }

    public int EndIndex { get; set; }

    public IList<string> Tags { get; set; } = Array.Empty<string>();

    public override bool Equals(object? obj) => this.Equals(obj as StateMatch);

    public bool Equals(StateMatch? other)
        => other is not null
        && this.StartIndex == other.StartIndex
        && this.EndIndex == other.EndIndex
        && this.Tags.SequenceEqual(other.Tags);

    public override int GetHashCode()
        => HashCode.Combine(this.StartIndex, this.EndIndex, this.Tags);
}
