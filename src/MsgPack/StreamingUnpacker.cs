﻿#region -- License Terms --
//
// MessagePack for CLI
//
// Copyright (C) 2010-2012 FUJIWARA, Yusuke
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
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MsgPack
{
	/// <summary>
	///		Implements streaming unpacking. This object is stateful.
	/// </summary>
	internal sealed class StreamingUnpacker
	{
		private const int _assumedCollectionItemSize = 4;

		/// <summary>
		///		Stacked states for context collection.
		/// </summary>
		private readonly CollectionUnpackagingState _collectionState = new CollectionUnpackagingState();

		/// <summary>
		///		Context header of unpackaging message.
		/// </summary>
		private MessagePackHeader _contextValueHeader;

		/// <summary>
		///		Buffer for unpackaging scalar or binary value.
		/// </summary>
		private BytesBuffer _bytesBuffer;

		public bool IsInRoot
		{
			get { return this._collectionState.IsEmpty; }
		}

		private bool _hasMoreEntries;

		public bool HasMoreEntries
		{
			get { return this._hasMoreEntries; }
		}

		public void SetEndCollection()
		{
			this._hasMoreEntries = true;
		}

		public uint UnpackingItemsCount
		{
			get { return this._lastEmptyCollection != EmptyCollectionType.None ? 0 : this._collectionState.UnpackingItemsCount; }
		}

		private EmptyCollectionType _lastEmptyCollection;
		private bool _isInCollection;

		public bool IsInArrayHeader
		{
			get
			{
				switch ( this._lastEmptyCollection )
				{
					case EmptyCollectionType.Array:
					{
						return true;
					}
					case EmptyCollectionType.Map:
					case EmptyCollectionType.Raw:
					{
						return false;
					}
					default:
					{
						if ( this._isInCollection )
						{
							return this._collectionState.UnpackedItemsCount == 0 && ( this._collectionState.IsArray );
						}
						return false;
					}
				}
			}
		}

		public bool IsInMapHeader
		{
			get
			{
				switch ( this._lastEmptyCollection )
				{
					case EmptyCollectionType.Array:
					case EmptyCollectionType.Raw:
					{
						return false;
					}
					case EmptyCollectionType.Map:
					{
						return true;
					}
					default:
					{
						if ( this._isInCollection )
						{
							return this._collectionState.UnpackedItemsCount == 0 && ( this._collectionState.IsMap );
						}

						return false;
					}
				}
			}
		}

		private delegate bool Unpacking( Stream source, UnpackingMode unpackingMode, out MessagePackObject? unpacked );

		private Unpacking _next;

		// TODO: Instrument that caching is truely effective.
		private readonly Unpacking _unpackHeader;
		private readonly Unpacking _unpackCollectionLength;
		private readonly Unpacking _unpackRawLength;
		private readonly Unpacking _unpackRawBytes;
		private readonly Unpacking _unpackScalar;

		private long _readByteLength;

		/// <summary>
		///		Initializes a new instance of the <see cref="StreamingUnpacker"/> class.
		/// </summary>
		public StreamingUnpacker()
		{
			this._unpackCollectionLength = this.UnpackCollectionLength;
			this._unpackHeader = this.UnpackHeader;
			this._unpackRawLength = this.UnpackRawLength;
			this._unpackRawBytes = this.UnpackRawBytes;
			this._unpackScalar = this.UnpackScalar;
			this._next = this._unpackHeader;
		}

		/// <summary>
		///		Try unpack object from specified source.
		/// </summary>
		/// <param name="source">Input source to unpack.</param>
		/// <param name="unpackingMode"><see cref="UnpackingMode"/> that controls unpacking flow.</param>
		/// <returns>
		///		Unpacked entry. The mean of the entry depends on specified <paramref name="unpackingMode"/>.
		/// </returns>
		/// <remarks>
		///		<para>
		///			When this method returns null, caller can feed extra bytes to <paramref name="source"/> and invoke this again. 
		///			It could succeed because this instance preserves previous invocation state, and required bytes are supplied.
		///		</para>
		///		<para>
		///			When this method completes unpackaging single <see cref="MessagePackObject"/> tree,
		///			this method stops iterating <paramref name="source"/> (via <see cref="IEnumerator&lt;T&gt;"/>.
		///			This behavior is notified via <see cref="IDisposable.Dispose">IEnumerator&lt;T&gt;.Dispose()</see> method.
		///		</para>
		/// </remarks>
		public MessagePackObject? Unpack( Stream source, UnpackingMode unpackingMode )
		{
			// FIXME:BULK LOAD
			Contract.Assert( source != null );

			this._lastEmptyCollection = EmptyCollectionType.None;

			MessagePackObject? collectionItemOrRoot;
			while ( this._next( source, unpackingMode, out collectionItemOrRoot ) )
			{
				if ( collectionItemOrRoot != null )
				{
					int depth = this._collectionState.Depth;
					var root = this.AddToContextCollection( collectionItemOrRoot.Value );
					this._hasMoreEntries = depth <= this._collectionState.Depth;

					if ( root != null )
					{
#if DEBUG
						Contract.Assert( this._collectionState.IsEmpty );
#endif
						this._next = this._unpackHeader;
						this._isInCollection = false;
#if DEBUG
						Contract.Assert( this._contextValueHeader.Type == MessageType.Unknown, this._contextValueHeader.ToString() );// null
						Contract.Assert( this._bytesBuffer.BackingStore == null, this._bytesBuffer.ToString() ); // null
#endif
						if ( unpackingMode == UnpackingMode.PerEntry )
						{
							if ( this._lastEmptyCollection != EmptyCollectionType.None )
							{
								// It was empty collection.
								return 0;
							}
							else
							{
								// Last item
								return collectionItemOrRoot.Value;
							}
						}
						else
						{
							// Length
							var readByteLength = this._readByteLength;
							this._readByteLength = 0L;
							return readByteLength;
						}
					}
				}
				else
				{
					this._hasMoreEntries = true;
				}

				// There are more entries in the tree.
				if ( unpackingMode == UnpackingMode.PerEntry )
				{
					// The unpacker may return unpacked collection item.
					if ( this._isInCollection )
					{
						if ( this._collectionState.UnpackedItemsCount == 0 )
						{
							// Count
							return this._collectionState.UnpackingItemsCount;
						}
						else if ( this._lastEmptyCollection != EmptyCollectionType.None )
						{
							// Empty nested collection.
							return 0;
						}
						else
						{
							Contract.Assert( collectionItemOrRoot.HasValue );
							return collectionItemOrRoot.Value;
						}
					}
				}
			}

			return null;
		}

		private delegate bool HeaderUnpacking( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result );

		private static readonly HeaderUnpacking[] _headerUnpackings = InitializeHeaderUnpackings();

		private static HeaderUnpacking[] InitializeHeaderUnpackings()
		{
			HeaderUnpacking[] result = new HeaderUnpacking[ 256 ];
			for ( int i = 0; i < 0x80; i++ )
			{
				result[ i ] = UnpackPositiveFixNum;
			}
			result[ 0x80 ] = UnpackEmptyMap;
			for ( int i = 0x81; i < 0x90; i++ )
			{
				result[ i ] = UnpackFixMapLength;
			}
			result[ 0x90 ] = UnpackEmptyArray;
			for ( int i = 0x91; i < 0xa0; i++ )
			{
				result[ i ] = UnpackFixArrayLength;
			}
			result[ 0xa0 ] = UnpackEmptyRaw;
			for ( int i = 0xa0; i < 0xc0; i++ )
			{
				result[ i ] = UnpackFixRawLength;
			}
			result[ 0xc0 ] = UnpackNil;
			// 0xc1 : Undefined
			result[ 0xc1 ] = ThrowInvalidHeaderException;
			result[ 0xc2 ] = UnpackFalse;
			result[ 0xc3 ] = UnpackTrue;
			// 0xc4-0xc9 : Undefined
			for ( int i = 0xc4; i < 0xca; i++ )
			{
				result[ i ] = ThrowInvalidHeaderException;
			}
			result[ 0xca ] = UnpackScalarHeader;
			result[ 0xcb ] = UnpackScalarHeader;
			result[ 0xcc ] = UnpackScalarHeader;
			result[ 0xcd ] = UnpackScalarHeader;
			result[ 0xce ] = UnpackScalarHeader;
			result[ 0xcf ] = UnpackScalarHeader;
			result[ 0xd0 ] = UnpackScalarHeader;
			result[ 0xd1 ] = UnpackScalarHeader;
			result[ 0xd2 ] = UnpackScalarHeader;
			result[ 0xd3 ] = UnpackScalarHeader;
			// 0xd4-0xd9 : Undefined
			for ( int i = 0xd4; i < 0xda; i++ )
			{
				result[ i ] = ThrowInvalidHeaderException;
			}
			result[ 0xda ] = UnpackRawHeader;
			result[ 0xdb ] = UnpackRawHeader;
			result[ 0xdc ] = UnpackArrayOrMapHeader;
			result[ 0xdd ] = UnpackArrayOrMapHeader;
			result[ 0xde ] = UnpackArrayOrMapHeader;
			result[ 0xdf ] = UnpackArrayOrMapHeader;
			for ( int i = 0xe0; i < 0x100; i++ )
			{
				result[ i ] = UnpackNegativeFixNum;
			}

			return result;
		}

		private static readonly MessagePackObject?[] _positveFixNums =
			Enumerable.Range( 0, 0x80 ).Select( i => new MessagePackObject?( unchecked( ( byte )i ) ) ).ToArray();

		private static bool UnpackPositiveFixNum( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			result = _positveFixNums[ b & 0x7f ];
			return true;
		}

		private static readonly MessagePackObject?[] _negavieFixNums =
			Enumerable.Range( 0xe0, 0x20 ).Select( i => new MessagePackObject?( unchecked( ( sbyte )( i - 0x100 ) ) ) ).ToArray();

		private static bool UnpackNegativeFixNum( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			result = _negavieFixNums[ b & 0x1f ];
			return true;
		}

		private static readonly MessagePackObjectDictionary _emptyMap = new MessagePackObjectDictionary( 0 ).Freeze();

		private static bool UnpackEmptyMap( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			result = new MessagePackObject( _emptyMap, true );
			@this._lastEmptyCollection = EmptyCollectionType.Map;
			return true;
		}

		private static bool UnpackFixMapLength( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			var header = _headerArray[ b ];
			@this._collectionState.NewContextCollection( header, header.ValueOrLength );
			result = null;
			@this.TransitToUnpackContextCollection();
			return true;
		}

		private static readonly MessagePackObject[] _emptyArray = new MessagePackObject[ 0 ];

		private static bool UnpackEmptyArray( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			result = new MessagePackObject( _emptyArray, true );
			@this._lastEmptyCollection = EmptyCollectionType.Array;
			return true;
		}

		private static bool UnpackFixArrayLength( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			var header = _headerArray[ b ];
			@this._collectionState.NewContextCollection( header, header.ValueOrLength );
			result = null;
			@this.TransitToUnpackContextCollection();
			return true;
		}

		private static readonly MessagePackObject? _emptyBinary = Binary.Empty;

		private static bool UnpackEmptyRaw( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			result = _emptyBinary;
			@this._lastEmptyCollection = EmptyCollectionType.Raw;
			return true;
		}

		private static bool UnpackFixRawLength( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			@this.TransitToUnpackRawBytes( unchecked( ( uint )( b & 0x1f ) ) );
			// Try to get body.
			return @this.UnpackRawBytes( source, unpackingMode, out result );
		}
		private static readonly MessagePackObject? _nil = MessagePackObject.Nil;

		private static bool UnpackNil( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			result = _nil;
			return true;
		}

		private static readonly MessagePackObject? _false = false;

		private static bool UnpackFalse( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			result = _false;
			return true;
		}

		private static readonly MessagePackObject? _true = true;

		private static bool UnpackTrue( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			result = _true;
			return true;
		}

		private static readonly BytesBuffer[] _scalarBuffers = new[] { new BytesBuffer( 1 ), new BytesBuffer( 2 ), new BytesBuffer( 4 ), new BytesBuffer( 8 ) };

		private static bool UnpackScalarHeader( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			// Transit to UnpackScalar
			@this._next = @this._unpackScalar;
			@this._isInCollection = false;
			@this._bytesBuffer = _scalarBuffers[ b % 4 ];

			// Try to get body.
			if ( !@this.UnpackScalar( source, unpackingMode, out result ) )
			{
				// Need more data
				return false;
			}

			return true;
		}

		private static bool UnpackRawHeader( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			@this._next = @this._unpackRawLength;
			@this._isInCollection = false;
			@this._bytesBuffer = ( b % 2 ) == 0 ? BytesBuffer.TwoBytes : BytesBuffer.FourBytes;

			// Try to get length.
			return @this.UnpackRawLength( source, unpackingMode, out result );
		}

		private static bool UnpackArrayOrMapHeader( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			// Transit to UnpackCollectionLength
			@this._next = @this._unpackCollectionLength;
			@this._isInCollection = false;
			@this._bytesBuffer = ( b % 2 ) == 0 ? BytesBuffer.TwoBytes : BytesBuffer.FourBytes;

			if ( !@this.UnpackCollectionLength( source, unpackingMode, out result ) )
			{
				// Need to get more data
				return false;
			}

			return true;
		}

		private static bool ThrowInvalidHeaderException( StreamingUnpacker @this, int b, Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			throw new MessageTypeException( String.Format( CultureInfo.CurrentCulture, "Header '0x{0:x2}' is not available.", b ) );
		}

		private bool UnpackHeader( Stream source, UnpackingMode unpackingMode, out MessagePackObject? result )
		{
			var b = source.ReadByte();
			if ( b < 0 )
			{
				result = null;
				return false;
			}

			this._readByteLength++;
			this._contextValueHeader = _headerArray[ b ];
			return _headerUnpackings[ b ]( this, b, source, unpackingMode, out result );
		}

		private bool UnpackScalar( Stream source, UnpackingMode unpackingMode, out MessagePackObject? unpacked )
		{
#if DEBUG
			Contract.Assert( ( this._contextValueHeader.Type & MessageType.IsVariable ) != 0, this._contextValueHeader.ToString() );
			Contract.Assert( ( this._contextValueHeader.Type & MessageType.IsCollection ) == 0, this._contextValueHeader.ToString() );
			Contract.Assert( this._bytesBuffer.BackingStore != null, this._bytesBuffer.ToString() );
#endif

			int feeded;
			this._bytesBuffer = this._bytesBuffer.Feed( source, out feeded );
			this._readByteLength += feeded;

			if ( this._bytesBuffer.IsFilled )
			{
				if ( unpackingMode == UnpackingMode.SkipSubtree )
				{
					// Set dummy
					unpacked = MessagePackObject.Nil;
				}
				else
				{
					unpacked = this._bytesBuffer.AsMessagePackObject( this._contextValueHeader.Type );
				}

				return true;
			}

			// Need more data.
			unpacked = null;
			return false;
		}

		private bool UnpackCollectionLength( Stream source, UnpackingMode unpackingMode, out MessagePackObject? unpacked )
		{
			int feeded;
			this._bytesBuffer = this._bytesBuffer.Feed( source, out feeded );
			this._readByteLength += feeded;

			if ( this._bytesBuffer.IsFilled )
			{
				// new collection

				var length = this._bytesBuffer.AsUInt32();
				if ( length == 0 )
				{
					// empty collection
					if ( unpackingMode == UnpackingMode.SkipSubtree )
					{
						// Set dummy
						unpacked = MessagePackObject.Nil;
					}
					else
					{
						unpacked = CreateEmptyCollection( this._contextValueHeader );
					}

					this._lastEmptyCollection = ToEmptyCollectionType( this._contextValueHeader.Type );
					return true;
				}

				this._collectionState.NewContextCollection( this._contextValueHeader, length );
				this.TransitToUnpackContextCollection();

				unpacked = null;
				return true;
			}

			// Try next iteration.
			unpacked = null;
			return false;
		}

		private static EmptyCollectionType ToEmptyCollectionType( MessageType messageType )
		{
			switch ( messageType )
			{
				case MessageType.Array16:
				case MessageType.Array32:
				case MessageType.FixArray:
				{
					return EmptyCollectionType.Array;
				}
				case MessageType.Map16:
				case MessageType.Map32:
				case MessageType.FixMap:
				{
					return EmptyCollectionType.Map;
				}
				case MessageType.Raw16:
				case MessageType.Raw32:
				case MessageType.FixRaw:
				{
					return EmptyCollectionType.Raw;
				}
				default:
				{
					return EmptyCollectionType.None;
				}
			}
		}

		private bool UnpackRawLength( Stream source, UnpackingMode unpackingMode, out MessagePackObject? unpacked )
		{
			int feeded;
			this._bytesBuffer = this._bytesBuffer.Feed( source, out feeded );
			this._readByteLength += feeded;

			if ( this._bytesBuffer.IsFilled )
			{
				var length = this._bytesBuffer.AsUInt32();
				if ( length == 0 )
				{
					// empty collection
					if ( unpackingMode == UnpackingMode.SkipSubtree )
					{
						// Set dummy
						unpacked = MessagePackObject.Nil;
					}
					else
					{
						unpacked = CreateEmptyCollection( this._contextValueHeader );
					}

					// Empty raw is not considered as EmptyCollection.
					return true;
				}

				this.TransitToUnpackRawBytes( length );

				return this.UnpackRawBytes( source, unpackingMode, out unpacked );
			}

			// Need more info.
			unpacked = null;
			return false;
		}

		private bool UnpackRawBytes( Stream source, UnpackingMode unpackingMode, out MessagePackObject? unpacked )
		{
#if DEBUG
			Contract.Assert( this._bytesBuffer.BackingStore != null, this._bytesBuffer.ToString() );
#endif

			int feeded;
			this._bytesBuffer = this._bytesBuffer.Feed( source, out feeded );
			this._readByteLength += feeded;
			if ( this._bytesBuffer.IsFilled )
			{
				if ( unpackingMode == UnpackingMode.SkipSubtree )
				{
					// Set dummy
					unpacked = MessagePackObject.Nil;
				}
				else
				{
					unpacked = this._bytesBuffer.AsMessagePackObject( this._contextValueHeader.Type );
				}

				return true;
			}

			// Need more info.
			unpacked = null;
			return false;
		}

		/// <summary>
		///		Transit current stage to unpackRawBytes stage with cleanuping states.
		/// </summary>
		/// <param name="length">The known length of the source.</param>
		private void TransitToUnpackRawBytes( uint length )
		{
			this._next = this._unpackRawBytes;
			this._isInCollection = false;
			// Allocate buffer to store raw binaries.
			this._bytesBuffer = new BytesBuffer( length );
		}

		/// <summary>
		///		Transit current stage to unpackContextCollection stage with cleanuping states.
		/// </summary>
		private void TransitToUnpackContextCollection()
		{
			this._next = this._unpackHeader;
			this._isInCollection = true;
			this._contextValueHeader = MessagePackHeader.Null;
			this._bytesBuffer = BytesBuffer.Null;
		}

		/// <summary>
		///		Create <see cref="MessagePackObject"/> which wraps appropriate empty collection.
		/// </summary>
		/// <param name="header">Header which has type information.</param>
		/// <returns><see cref="MessagePackObject"/> which wraps appropriate empty collection.</returns>
		private static MessagePackObject CreateEmptyCollection( MessagePackHeader header )
		{
			Contract.Assert( header.ValueOrLength == 0, header.ToString() );

			if ( ( header.Type & MessageType.IsArray ) != 0 )
			{
				return new MessagePackObject( _emptyArray, true );
			}
			else if ( ( header.Type & MessageType.IsMap ) != 0 )
			{
				return new MessagePackObject( _emptyMap, true );
			}
			else
			{
				return _emptyBinary.Value;
			}
		}

		private static readonly MessagePackHeader[] _headerArray = InitializeHeaderArray();

		private static MessagePackHeader[] InitializeHeaderArray()
		{
			MessagePackHeader[] result = new MessagePackHeader[ 0x100 ];

			for ( int i = 0; i < 0x80; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.PositiveFixNum, i );
			}
			for ( int i = 0x80; i < 0x90; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.FixMap, i & 0x0f );
			}
			for ( int i = 0x90; i < 0xa0; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.FixArray, i & 0x0f );
			}
			for ( int i = 0xa0; i < 0xc0; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.FixRaw, i & 0x1f );
			}
			result[ 0xc0 ] = MessageType.Nil;
			// 0xc1 : Undefined
			result[ 0xc2 ] = MessageType.False;
			result[ 0xc3 ] = MessageType.True;
			// 0xc4-0xc9 : Undefined
			result[ 0xca ] = MessageType.Single; // 1
			result[ 0xcb ] = MessageType.Double;
			result[ 0xcc ] = MessageType.UInt8;
			result[ 0xcd ] = MessageType.UInt16;
			result[ 0xce ] = MessageType.UInt32;
			result[ 0xcf ] = MessageType.UInt64;
			result[ 0xd0 ] = MessageType.Int8;
			result[ 0xd1 ] = MessageType.Int16;
			result[ 0xd2 ] = MessageType.Int32;
			result[ 0xd3 ] = MessageType.Int64;
			// 0xd4-0xd9 : Undefined
			result[ 0xda ] = MessageType.Raw16;
			result[ 0xdb ] = MessageType.Raw32;
			result[ 0xdc ] = MessageType.Array16;
			result[ 0xdd ] = MessageType.Array32;
			result[ 0xde ] = MessageType.Map16;
			result[ 0xdf ] = MessageType.Map32;
			for ( int i = 0xe0; i < 0x100; i++ )
			{
				result[ i ] = new MessagePackHeader( MessageType.NegativeFixNum, i & 0x1f );
			}

			return result;
		}

		/// <summary>
		///		Add unpacked item to context collection.
		///		If context collection is fulfilled, then return it.
		/// </summary>
		/// <param name="item">Item to be added to context collection.</param>
		/// <returns>
		///		If context collection is fulfilled, then return it.
		///		Otherwise null.
		/// </returns>
		private MessagePackObject? AddToContextCollection( MessagePackObject item )
		{
			MessagePackObject current = item;
			while ( !this._collectionState.IsEmpty )
			{
				if ( !this._collectionState.FeedItem( current ) )
				{
					this.TransitToUnpackContextCollection();
					return null;
				}

				current = this._collectionState.PopCollection();
			}

			this.TransitToUnpackContextCollection();
			return current;
		}

		/// <summary>
		///		Represents type of message.
		/// </summary>
		private enum MessageType : ushort
		{
			/// <summary>
			///		Type is not known yet.
			/// </summary>
			Unknown = 0,
			Nil = 10,
			PositiveFixNum = 20,
			NegativeFixNum = 21,
			UInt8 = IsVariable | 30,
			UInt16 = IsVariable | 31,
			UInt32 = IsVariable | 32,
			UInt64 = IsVariable | 33,
			Int8 = IsVariable | 40,
			Int16 = IsVariable | 41,
			Int32 = IsVariable | 42,
			Int64 = IsVariable | 43,
			FixRaw = IsRawBinary | IsCollection | 50,
			Raw16 = IsVariable | IsRawBinary | IsCollection | 51,
			Raw32 = IsVariable | IsRawBinary | IsCollection | 52,
			Single = IsVariable | 60,
			Double = IsVariable | 61,
			False = 70,
			True = 71,
			FixArray = IsArray | IsCollection | 80,
			Array16 = IsVariable | IsArray | IsCollection | 81,
			Array32 = IsVariable | IsArray | IsCollection | 82,
			FixMap = IsMap | IsCollection | 90,
			Map16 = IsVariable | IsMap | IsCollection | 91,
			Map32 = IsVariable | IsMap | IsCollection | 92,
			// TODO: string
			// TODO: Fixed-Typed Array

			/// <summary>
			///		Flag indicates type is variable, so length unpacking is required.
			/// </summary>
			IsVariable = 0x400,

			/// <summary>
			///		Flag indicates type is collection, so context collection management for nesting is required.
			/// </summary>
			IsCollection = 0x800,

			/// <summary>
			///		Flag indicates type is a type of array.
			/// </summary>
			IsArray = 0x1000,

			/// <summary>
			///		Flag indicates type is a type of map.
			/// </summary>
			IsMap = 0x2000,

			/// <summary>
			///		Flag indicates type is a type of raw binary.
			/// </summary>
			IsRawBinary = 0x4000,
		}

		/// <summary>
		///		Lightweight structured header representation.
		///		Note that this is VALUE type.
		/// </summary>
		private struct MessagePackHeader
		{
			/// <summary>
			///		Null value.
			/// </summary>
			public static readonly MessagePackHeader Null = new MessagePackHeader();

			private readonly MessageType _type;

			/// <summary>
			///		Get type of message.
			/// </summary>
			/// <value>Type of message.</value>
			public MessageType Type
			{
				get { return this._type; }
			}

			private readonly uint _valueOrLength;

			/// <summary>
			///		Get value of fixed scalar value, length of fixed collections,
			///		length of fixed raw binary, or length of variable length.
			/// </summary>
			public uint ValueOrLength
			{
				get { return this._valueOrLength; }
			}

			public MessagePackHeader( MessageType type, int valueOrLength )
				: this( type, ToUInt32( valueOrLength ) ) { }

			private static uint ToUInt32( int valueOrLength )
			{
				Contract.Assert( valueOrLength >= 0 );
				return unchecked( ( uint )valueOrLength );
			}

			public MessagePackHeader( MessageType type, uint valueOrLength )
			{
				this._type = type;
				this._valueOrLength = valueOrLength;
			}

			public override string ToString()
			{
				return this._type + ":" + this._valueOrLength;
			}

			public static implicit operator MessagePackHeader( MessageType type )
			{
				return new MessagePackHeader( type, 0 );
			}
		}

		/// <summary>
		///		Represents a set of states for unpackaging context collection.
		/// </summary>
		private sealed class CollectionUnpackagingState
		{
			/// <summary>
			///		Stack of collection context.
			/// </summary>
			private readonly Stack<CollectionContextState> _collectionContextStack = new Stack<CollectionContextState>();

			/// <summary>
			///		Get the value indicates whether internal context stack is empty.
			/// </summary>
			/// <value>
			///		If internal context stack is empty then true.
			/// </value>
			/// <remarks>
			///		If this property returns true when you complete unpackaging context collection,
			///		it indicates that entire object tree has been unpackaged.
			/// </remarks>
			public bool IsEmpty
			{
				get { return this._collectionContextStack.Count == 0; }
			}

			/// <summary>
			///		Gets the unpacking items count.
			/// </summary>
			/// <value>
			///		The unpacking items count.
			/// </value>
			public uint UnpackingItemsCount
			{
				get { return this._collectionContextStack.Peek().Capacity; }
			}

			/// <summary>
			///		Gets the unpacked items count.
			/// </summary>
			/// <value>
			///		The unpacked items count.
			/// </value>
			public uint UnpackedItemsCount
			{
				get { return this._collectionContextStack.Peek().Unpacked; }
			}

			public bool IsArray
			{
				get { return this._collectionContextStack.Peek().IsArray; }
			}

			public bool IsMap
			{
				get { return this._collectionContextStack.Peek().IsMap; }
			}

			public int Depth
			{
				get { return this._collectionContextStack.Count; }
			}

			/// <summary>
			///		Initializes a new instance.
			/// </summary>
			public CollectionUnpackagingState() { }

			/// <summary>
			///		Push new context collection state to internal stack.
			/// </summary>
			/// <param name="header">Header of collection object.</param>
			/// <param name="count">Items count of collection object. If collection is map, this value indicates count of entries.</param>
			public void NewContextCollection( MessagePackHeader header, uint count )
			{
#if DEBUG
				Contract.Assert( ( header.Type & MessageType.IsRawBinary ) == 0, header.Type.ToString() );
#endif

				this._collectionContextStack.Push( CollectionContextState.Create( header, count ) );
			}

			/// <summary>
			///		Pop context collection state from internal stack, 
			///		and return <see cref="MessagePackObject"/> which represents popped context collection.
			/// </summary>
			/// <returns></returns>
			public MessagePackObject PopCollection()
			{
				return this._collectionContextStack.Pop().GetCollection();
			}

			/// <summary>
			///		Feed new collection item to context collection state.
			/// </summary>
			/// <param name="item">New item to feed.</param>
			public bool FeedItem( MessagePackObject item )
			{
				var top = this._collectionContextStack.Peek();
				top.AddUnpackedItem( item );
				return top.IsFilled;
			}

			public abstract class CollectionContextState
			{
				public abstract uint Capacity { get; }
				public abstract uint Unpacked { get; }
				public abstract bool IsFilled { get; }
				public abstract bool IsArray { get; }
				public abstract bool IsMap { get; }
				public abstract void AddUnpackedItem( MessagePackObject item );
				public abstract MessagePackObject GetCollection();

				public static CollectionContextState Create( MessagePackHeader header, uint count )
				{
					if ( ( header.Type & MessageType.IsArray ) != 0 )
					{
						return new ArrayForgettingCollectionContextState( count );
					}
					else if ( ( header.Type & MessageType.IsMap ) != 0 )
					{
						return new MapForgettingCollectionContextState( count );
					}
					else
					{
						throw new InvalidMessagePackStreamException();
					}
				}
			}

			public abstract class ForgettingCollectionContextState : CollectionContextState
			{
				private readonly uint _capacity;

				public sealed override uint Capacity
				{
					get { return this._capacity; }
				}

				private uint _unpacked;

				public sealed override uint Unpacked
				{
					get { return this._unpacked; }
				}

				protected ForgettingCollectionContextState( uint count )
				{
					this._capacity = count;
				}

				public sealed override void AddUnpackedItem( MessagePackObject item )
				{
					this._unpacked++;
				}

				public sealed override MessagePackObject GetCollection()
				{
#if DEBUG
					Contract.Assert( this.IsFilled );
#endif
					return MessagePackObject.Nil;
				}
			}

			public sealed class ArrayForgettingCollectionContextState : ForgettingCollectionContextState
			{
				public sealed override bool IsFilled
				{
					get { return this.Capacity == this.Unpacked; }
				}

				public sealed override bool IsArray
				{
					get { return true; }
				}

				public sealed override bool IsMap
				{
					get { return false; }
				}

				public ArrayForgettingCollectionContextState( uint count ) : base( count ) { }
			}

			public sealed class MapForgettingCollectionContextState : ForgettingCollectionContextState
			{
				public sealed override bool IsFilled
				{
					get { return this.Capacity * 2 == this.Unpacked; }
				}

				public sealed override bool IsArray
				{
					get { return false; }
				}

				public sealed override bool IsMap
				{
					get { return true; }
				}

				public MapForgettingCollectionContextState( uint count ) : base( count ) { }
			}
		}

		/// <summary>
		///		Represents buffer as value type.
		/// </summary>
		private struct BytesBuffer
		{
			public static readonly BytesBuffer TwoBytes = new BytesBuffer( 2 );
			public static readonly BytesBuffer FourBytes = new BytesBuffer( 4 );

			/// <summary>
			///		Represents null buffer.
			/// </summary>
			public static readonly BytesBuffer Null = new BytesBuffer();

			private readonly byte[] _backingStore;

#if DEBUG
			internal byte[] BackingStore
			{
				get { return this._backingStore; }
			}
#endif

			private readonly int _position;

			/// <summary>
			///		Get the value which indicates this buffer is filled.
			/// </summary>
			/// <value>If  this buffer is filled then true.</value>
			public bool IsFilled
			{
				get
				{
					return this._backingStore == null ? false : this._position == this._backingStore.Length;
				}
			}

			/// <summary>
			///		Initializes a new instance.
			/// </summary>
			/// <param name="length">Length of bytes.</param>
			public BytesBuffer( uint length )
			{
				if ( length > Int32.MaxValue )
				{
					throw new MessageNotSupportedException( "The raw binary which length is greater than Int32.MaxValue is not supported." );
				}

				this._backingStore = new byte[ length ];
				this._position = 0;
			}

			/// <summary>
			///		Initializes a new instance.
			/// </summary>
			/// <param name="backingStore">Existent backing store.</param>
			/// <param name="newPosition">Position where this buffer is filled.</param>
			private BytesBuffer( byte[] backingStore, int newPosition )
			{
				this._backingStore = backingStore;
				this._position = newPosition;
			}

			/// <summary>
			///		Returns string representation of this object.
			/// </summary>
			/// <returns>
			///		String which format is "byte[<em>Length</em>]@<em>Position</em>".
			/// </returns>
			public override string ToString()
			{
				if ( this._backingStore == null )
				{
					return "(null)";
				}
				else
				{
					return String.Format( CultureInfo.InvariantCulture, "byte[{0}]@{1}", this._backingStore.Length, this._position );
				}
			}

			/// <summary>
			///		Feed specified <see cref="Stream"/> to this buffer, and increment position.
			/// </summary>
			/// <param name="stream"><see cref="Stream"/> to be feeded.</param>
			/// <param name="feededLength">The actual read length is stored.</param>
			/// <returns>New buffer to replace this object.</returns>
			public BytesBuffer Feed( Stream stream, out int feededLength )
			{
				int reading = this._backingStore.Length - this._position;
				int actualRead = stream.Read( this._backingStore, this._position, reading );
				feededLength = actualRead;
				return new BytesBuffer( this._backingStore, this._position + actualRead );
			}

			/// <summary>
			///		Get internal value as <see cref="UInt32"/>.
			/// </summary>
			/// <returns><see cref="UInt32"/> value of this buffer.</returns>
			public uint AsUInt32()
			{
				Contract.Assert( this.IsFilled, "Not filled yet:" + this );

				switch ( this._backingStore.Length )
				{
					case 1:
					{
						return this._backingStore[ 0 ];
					}
					case 2:
					{
						return BigEndianBinary.ToUInt16( this._backingStore, 0 );
					}
					default:
					{
						Contract.Assert( this._backingStore.Length == sizeof( uint ), this._backingStore.Length.ToString() );
						return BigEndianBinary.ToUInt32( this._backingStore, 0 );
					}
				}
			}

			/// <summary>
			///		Get internal buffer as specified <see cref="MessagePackObject"/> numeric primitive.
			/// </summary>
			/// <param name="type">Type of value to be deserialized.</param>
			/// <returns><see cref="MessagePackObject"/> which wraps deserialized numeric primitive.</returns>
			public MessagePackObject AsMessagePackObject( MessageType type )
			{
#if DEBUG
				Contract.Assert( this.IsFilled, "Not filled yet:" + this );
#endif

				return AsMessagePackObject( this._backingStore, type );
			}

			public static MessagePackObject AsMessagePackObject( byte[] buffer, MessageType type )
			{
				switch ( type )
				{
					case MessageType.Double:
					{
						return new MessagePackObject( BigEndianBinary.ToDouble( buffer, 0 ) );
					}
					case MessageType.Int16:
					{
						return new MessagePackObject( BigEndianBinary.ToInt16( buffer, 0 ) );
					}
					case MessageType.Int32:
					{
						return new MessagePackObject( BigEndianBinary.ToInt32( buffer, 0 ) );
					}
					case MessageType.Int64:
					{
						return new MessagePackObject( BigEndianBinary.ToInt64( buffer, 0 ) );
					}
					case MessageType.Int8:
					{
						return new MessagePackObject( BigEndianBinary.ToSByte( buffer, 0 ) );
					}
					case MessageType.Single:
					{
						return new MessagePackObject( BigEndianBinary.ToSingle( buffer, 0 ) );
					}
					case MessageType.UInt16:
					{
						return new MessagePackObject( BigEndianBinary.ToUInt16( buffer, 0 ) );
					}
					case MessageType.UInt32:
					{
						return new MessagePackObject( BigEndianBinary.ToUInt32( buffer, 0 ) );
					}
					case MessageType.UInt64:
					{
						return new MessagePackObject( BigEndianBinary.ToUInt64( buffer, 0 ) );
					}
					case MessageType.UInt8:
					{
						return new MessagePackObject( BigEndianBinary.ToByte( buffer, 0 ) );
					}
					default:
					{
						return new MessagePackObject( buffer );
					}
				}
			}
		}

		private enum EmptyCollectionType
		{
			None,
			Array,
			Map,
			Raw
		}
	}
}