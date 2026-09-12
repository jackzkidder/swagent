using System;
using System.Collections.Generic;
using System.Globalization;

namespace SwAgent.Harness
{
    /// <summary>
    /// A deliberately tiny test reporter.
    ///
    /// No test framework, because this harness must run as a plain x64 STA
    /// console process that owns its SOLIDWORKS thread, and test runners like
    /// to own threading themselves.
    /// </summary>
    public sealed class TestRun
    {
        private readonly List<string> _failures = new List<string>();
        private string _currentTest = "(none)";
        private string _currentStep = "(none)";

        public int Passed { get; private set; }
        public int Failed => _failures.Count;

        public void BeginTest(string name)
        {
            _currentTest = name;
            Console.WriteLine();
            Console.WriteLine($"== {name}");
        }

        public void Step(string what)
        {
            _currentStep = what;
            Console.WriteLine($"   -> {what}");
        }

        public void Note(string what)
        {
            Console.WriteLine($"      {what}");
        }

        public void Assert(bool condition, string description)
        {
            if (condition)
            {
                Passed++;
                Console.WriteLine($"      PASS  {description}");
            }
            else
            {
                Fail($"{description} (during: {_currentStep})");
            }
        }

        /// <summary>Assert two values agree within a relative tolerance.</summary>
        public void AssertClose(double actual, double expected, double relativeTolerance, string description)
        {
            double allowed = Math.Abs(expected) * relativeTolerance;
            double delta = Math.Abs(actual - expected);
            var c = CultureInfo.InvariantCulture;

            if (delta <= allowed)
            {
                Passed++;
                Console.WriteLine(
                    $"      PASS  {description}  (got {actual.ToString("0.#####", c)})");
            }
            else
            {
                Fail($"{description}: expected {expected.ToString("0.#####", c)}, " +
                     $"got {actual.ToString("0.#####", c)} " +
                     $"(off by {delta.ToString("0.#####", c)}, tolerance {allowed.ToString("0.#####", c)})");
            }
        }

        /// <summary>Assert that a call is rejected rather than carried out.</summary>
        public void AssertThrows(Action action, string description)
        {
            try
            {
                action();
                Fail($"{description}: the call was ACCEPTED when it should have been rejected");
            }
            catch (Exception ex)
            {
                Passed++;
                Console.WriteLine($"      PASS  {description}");
                Console.WriteLine($"            rejected with: {ex.Message}");
            }
        }

        public void Fail(string description)
        {
            _failures.Add($"[{_currentTest}] {description}");
            Console.WriteLine($"      FAIL  {description}");
        }

        public int Summarize()
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 68));

            if (_failures.Count == 0)
            {
                Console.WriteLine($"ALL PASSED  ({Passed} assertions)");
                return 0;
            }

            Console.WriteLine($"FAILED  ({Passed} passed, {_failures.Count} failed)");
            foreach (var f in _failures)
                Console.WriteLine($"  - {f}");

            return 1;
        }
    }
}
