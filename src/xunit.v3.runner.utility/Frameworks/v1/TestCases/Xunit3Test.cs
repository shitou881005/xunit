﻿#if NETFRAMEWORK

using Xunit.Sdk;
using Xunit.v3;

namespace Xunit.Runner.v1
{
	/// <summary>
	/// An implementation of <see cref="_ITest"/> for xUnit v1.
	/// </summary>
	public class Xunit3Test : _ITest
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="Xunit3Test"/> class.
		/// </summary>
		/// <param name="testCase">The test case this test belongs to.</param>
		/// <param name="displayName">The display name for this test.</param>
		/// <param name="testIndex">The index of this test within the test case. Used to
		/// compute the <see cref="UniqueID"/>.</param>
		public Xunit3Test(
			_ITestCase testCase,
			string displayName,
			int testIndex)
		{
			TestCase = testCase;
			DisplayName = displayName;
			UniqueID = UniqueIDGenerator.ForTest(testCase.UniqueID, testIndex);
		}

		/// <inheritdoc/>
		public string DisplayName { get; }

		/// <inheritdoc/>
		public _ITestCase TestCase { get; }

		/// <inheritdoc/>
		public string UniqueID { get; }
	}
}

#endif
