﻿#region -- License Terms --
//
// MessagePack for CLI
//
// Copyright (C) 2017 FUJIWARA, Yusuke
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//
#endregion -- License Terms --

using System;
using System.Collections.Generic;
#if !MSTEST
using NUnit.Framework;
#else
using TestFixtureAttribute = Microsoft.VisualStudio.TestPlatform.UnitTestFramework.TestClassAttribute;
using TestAttribute = Microsoft.VisualStudio.TestPlatform.UnitTestFramework.TestMethodAttribute;
using TimeoutAttribute = NUnit.Framework.TimeoutAttribute;
using Assert = NUnit.Framework.Assert;
using Is = NUnit.Framework.Is;
#endif

namespace MsgPack
{
	[TestFixture]
	[Timeout( 3000 )]
	public class CollectionValidatingByteArrayUnpackerTest : ByteArrayUnpackerTest
	{
		protected override bool ShouldCheckSubtreeUnpacker
		{
			get { return true; }
		}

		protected override ByteArrayUnpacker CreateUnpacker( ArraySegment<byte> source )
		{
			return Unpacker.Create( source, new UnpackerOptions { ValidationLevel = UnpackerValidationLevel.Collection } );
		}

		protected override ByteArrayUnpacker CreateUnpacker( IList<ArraySegment<byte>> sources )
		{
			return Unpacker.Create( sources, 0, 0, new UnpackerOptions { ValidationLevel = UnpackerValidationLevel.Collection } );
		}
	}
}
