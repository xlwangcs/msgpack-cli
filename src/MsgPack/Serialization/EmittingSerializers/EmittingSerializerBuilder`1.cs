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
using System.Linq;
using System.Reflection.Emit;
using MsgPack.Serialization.Reflection;

namespace MsgPack.Serialization.EmittingSerializers
{
	/// <summary>
	///		Implements common features code generation based serializer builders.
	/// </summary>
	/// <typeparam name="TObject">The type of the serialization target.</typeparam>
	internal abstract class EmittingSerializerBuilder<TObject> : SerializerBuilder<TObject>
	{
		private readonly EmitterFlavor _emitterFlavor;

		private SerializationMethodGeneratorManager _generatorManager;

		internal SerializationMethodGeneratorManager GeneratorManager
		{
			get { return this._generatorManager; }
			set { this._generatorManager = value; }
		}

		/// <summary>
		///		Initializes a new instance of the <see cref="EmittingSerializerBuilder&lt;TObject&gt;"/> class.
		/// </summary>
		/// <param name="context">The <see cref="SerializationContext"/>.</param>
		protected EmittingSerializerBuilder( SerializationContext context )
			: base( context )
		{
			this._emitterFlavor = context.EmitterFlavor;
			this._generatorManager = SerializationMethodGeneratorManager.Get( context.GeneratorOption );
		}

		/// <summary>
		///		Creates serializer for <typeparamref name="TObject"/>.
		/// </summary>
		/// <param name="entries">Serialization target members. This will not be <c>null</c> nor empty.</param>
		/// <returns>
		///		<see cref="MessagePackSerializer{T}"/>. This value will not be <c>null</c>.
		/// </returns>
		protected sealed override MessagePackSerializer<TObject> CreateSerializer( SerializingMember[] entries )
		{
			using ( var emitter = this._generatorManager.CreateEmitter( typeof( TObject ), this._emitterFlavor ) )
			{
				try
				{
					var packerIL = emitter.GetPackToMethodILGenerator();
					try
					{
						this.EmitPackMembers( emitter, packerIL, entries );
					}
					finally
					{
						packerIL.FlushTrace();
					}

					var unpackerIL = emitter.GetUnpackFromMethodILGenerator();
					try
					{
						// TODO: For big struct, use Dictionary<String,SM>
						var result = unpackerIL.DeclareLocal( typeof( TObject ), "result" );
						Emittion.EmitConstruction( unpackerIL, result, null );

						EmitUnpackMembers( emitter, unpackerIL, entries, result );

						unpackerIL.EmitAnyLdloc( result );
						unpackerIL.EmitRet();
					}
					finally
					{
						unpackerIL.FlushTrace();
					}

					return emitter.CreateInstance<TObject>( this.Context );
				}
				finally
				{
					emitter.FlushTrace();
				}
			}
		}

		/// <summary>
		///		Emits the ILs to pack the members of the current type.
		/// </summary>
		/// <param name="emitter"><see cref="SerializerEmitter"/> holding emittion context information.</param>
		/// <param name="packerIL"><see cref="TracingILGenerator"/> to emit IL.</param>
		/// <param name="entries">The array of <see cref="SerializingMember"/>s where each represents the member to be (de)serialized.</param>
		protected abstract void EmitPackMembers( SerializerEmitter emitter, TracingILGenerator packerIL, SerializingMember[] entries );

		private static void EmitUnpackMembers( SerializerEmitter emitter, TracingILGenerator unpackerIL, SerializingMember[] entries, LocalBuilder result )
		{
			/*
			 *	if( unpacker.IsArrayHeader )
			 *	{
			 *		...
			 *	}
			 *	else
			 *	{
			 *		...
			 *	}
			 */
			unpackerIL.EmitAnyLdarg( 1 );
			unpackerIL.EmitGetProperty( Metadata._Unpacker.IsArrayHeader );
			var @else = unpackerIL.DefineLabel( "ELSE" );
			var endif = unpackerIL.DefineLabel( "END_IF" );
			unpackerIL.EmitBrfalse( @else );
			EmitUnpackMembersFromArray( emitter, unpackerIL, entries, result );
			unpackerIL.EmitBr( endif );
			unpackerIL.MarkLabel( @else );
			EmitUnpackMembersFromMap( emitter, unpackerIL, entries, result );
			unpackerIL.MarkLabel( endif );
		}

		private static void EmitUnpackMembersFromArray( SerializerEmitter emitter, TracingILGenerator unpackerIL, SerializingMember[] entries, LocalBuilder result )
		{
			/*
			 *  int unpacked = 0;
			 *  int itemsCount = unpacker.ItemsCount;
			 * 
			 *  :
			 *  if( unpacked == itemsCount )
			 *  {
			 *		HandleNilImplication(...);
			 *  }
			 *  else
			 *  {
			 *		if( !unpacker.Read() )
			 *		{
			 *			throw SerializationExceptions.NewUnexpectedEndOfStreamMethod();
			 *		}
			 *		
			 *		local1 = this._serializer1.Unpack
			 *		unpacked++;
			 *	}
			 *	:
			 */

			// TODO: Supports ExtensionObject like round-tripping.

			var itemsCount = unpackerIL.DeclareLocal( typeof( int ), "itemsCount" );
			var unpacked = unpackerIL.DeclareLocal( typeof( int ), "unpacked" );
			Emittion.EmitGetUnpackerItemsCountAsInt32( unpackerIL, 1 );
			unpackerIL.EmitAnyStloc( itemsCount );

			var items =
				entries.Select(
					item =>
						new
						{
							Entry = item,
							UnpackedLocal =
								item.Member != null && !UnpackHelpers.IsReadOnlyAppendableCollectionMember( item.Member )
								? unpackerIL.DeclareLocal( item.Member.GetMemberValueType(), item.Contract.Name )
								: null,
							IsUnpackedLocal = unpackerIL.DeclareLocal( typeof( bool ), "is" + item.Contract.Name + "Unpacked" )
						}
				).ToArray();

			for ( int i = 0; i < items.Length; i++ )
			{
				var endIf0 = unpackerIL.DefineLabel( "END_IF" );
				var else0 = unpackerIL.DefineLabel( "ELSE" );

				unpackerIL.EmitAnyLdloc( unpacked );
				unpackerIL.EmitAnyLdloc( itemsCount );
				unpackerIL.EmitBlt( else0 );
				// Just skip for missing memeber.
				if ( items[ i ].Entry.Member != null )
				{
					// Respect nil implication.
					Emittion.EmitNilImplication( unpackerIL, 1, items[ i ].Entry.Contract.Name, items[ i ].Entry.Contract.NilImplication, endIf0 );
				}

				unpackerIL.EmitBr( endIf0 );

				unpackerIL.MarkLabel( else0 );

				unpackerIL.EmitAnyLdarg( 1 );
				unpackerIL.EmitAnyCall( Metadata._Unpacker.Read );
				var endIf = unpackerIL.DefineLabel( "END_IF" );
				unpackerIL.EmitBrtrue_S( endIf );
				unpackerIL.EmitAnyCall( SerializationExceptions.NewUnexpectedEndOfStreamMethod );
				unpackerIL.EmitThrow();
				unpackerIL.MarkLabel( endIf );
				if ( items[ i ].Entry.Member == null )
				{
					// Ignore undefined member -- Nop.
				}
				else if ( items[ i ].UnpackedLocal == null )
				{
					Emittion.EmitDeserializeCollectionValue( emitter, unpackerIL, 1, result, items[ i ].Entry.Member, items[ i ].Entry.Member.GetMemberValueType(), items[ i ].Entry.Contract.NilImplication );
				}
				else
				{
					Emittion.EmitDeserializeValue( emitter, unpackerIL, 1, items[ i ].UnpackedLocal, items[ i ].IsUnpackedLocal, items[ i ].Entry.Member.Name, items[ i ].Entry.Contract.NilImplication, null );
				}

				unpackerIL.EmitAnyLdloc( unpacked );
				unpackerIL.EmitLdc_I4_1();
				unpackerIL.EmitAdd();
				unpackerIL.EmitAnyStloc( unpacked );

				unpackerIL.MarkLabel( endIf0 );
			}

			foreach ( var item in items )
			{
				if ( item.UnpackedLocal == null )
				{
					continue;
				}

				// TODO: Mandatory -> Exception?
				var endIf = unpackerIL.DefineLabel( "END_IF" );
				unpackerIL.EmitAnyLdloc( item.IsUnpackedLocal );
				unpackerIL.EmitBrfalse_S( endIf );

				if ( result.LocalType.IsValueType )
				{
					unpackerIL.EmitAnyLdloca( result );
				}
				else
				{
					unpackerIL.EmitAnyLdloc( result );
				}

				unpackerIL.EmitAnyLdloc( item.UnpackedLocal );
				Emittion.EmitStoreValue( unpackerIL, item.Entry.Member );
				unpackerIL.MarkLabel( endIf );
			}

		}

		private static void EmitUnpackMembersFromMap( SerializerEmitter emitter, TracingILGenerator unpackerIL, SerializingMember[] entries, LocalBuilder result )
		{
			var items =
				entries.Select(
					item =>
						new
						{
							Entry = item,
							UnpackedLocal =
								item.Member != null && !UnpackHelpers.IsReadOnlyAppendableCollectionMember( item.Member )
								? unpackerIL.DeclareLocal( item.Member.GetMemberValueType(), item.Contract.Name )
								: null,
							IsUnpackedLocal = unpackerIL.DeclareLocal( typeof( bool ), "is" + item.Contract.Name + "Unpacked" )
						}
				).ToArray();

			/*
			 *	// Assume subtree unpacker
			 *	while( unpacker.Read() )
			 *	{
			 *		var memberName = unpacker.Data.AsString();
			 *		if( memberName == "A" )
			 *		{
			 *			if( !unpacker.Read() )
			 *			{
			 *				throw SerializationExceptions.NewUnexpectedStreamEndsException();
			 *			}
			 *			
			 *			isAFound = true;
			 *		}
			 *		:
			 *	}
			 */

			var whileCond = unpackerIL.DefineLabel( "WHILE_COND" );
			var endWhile = unpackerIL.DefineLabel( "END_WHILE" );
			unpackerIL.MarkLabel( whileCond );
			unpackerIL.EmitAnyLdarg( 1 );
			unpackerIL.EmitAnyCall( Metadata._Unpacker.Read );
			unpackerIL.EmitBrfalse( endWhile );

			var data = unpackerIL.DeclareLocal( typeof( MessagePackObject? ), "data" );
			var dataValue = unpackerIL.DeclareLocal( typeof( MessagePackObject ), "dataValue" );
			var memberName = unpackerIL.DeclareLocal( typeof( string ), "memberName" );
			unpackerIL.EmitAnyLdarg( 1 );
			unpackerIL.EmitGetProperty( Metadata._Unpacker.Data );
			unpackerIL.EmitAnyStloc( data );
			unpackerIL.EmitAnyLdloca( data );
			unpackerIL.EmitGetProperty( Metadata._Nullable<MessagePackObject>.Value );
			unpackerIL.EmitAnyStloc( dataValue );
			unpackerIL.EmitAnyLdloca( dataValue );
			unpackerIL.EmitAnyCall( Metadata._MessagePackObject.AsString );
			unpackerIL.EmitAnyStloc( memberName );
			for ( int i = 0; i < items.Length; i++ )
			{
				if ( items[ i ].Entry.Contract.Name == null )
				{
					// skip undefined member
					continue;
				}

				// TODO: binary comparison
				unpackerIL.EmitAnyLdloc( memberName );
				unpackerIL.EmitLdstr( items[ i ].Entry.Contract.Name );
				unpackerIL.EmitAnyCall( Metadata._String.op_Inequality );
				var endIf0 = unpackerIL.DefineLabel( "END_IF_0_" + i );
				unpackerIL.EmitBrtrue( endIf0 );
				unpackerIL.EmitAnyLdarg( 1 );
				unpackerIL.EmitAnyCall( Metadata._Unpacker.Read );
				var endIf1 = unpackerIL.DefineLabel( "END_IF_1_" + i );
				unpackerIL.EmitBrtrue_S( endIf1 );
				unpackerIL.EmitAnyCall( SerializationExceptions.NewUnexpectedEndOfStreamMethod );
				unpackerIL.EmitThrow();
				unpackerIL.MarkLabel( endIf1 );

				if ( items[ i ].UnpackedLocal == null )
				{
					Emittion.EmitDeserializeCollectionValue( emitter, unpackerIL, 1, result, items[ i ].Entry.Member, items[ i ].Entry.Member.GetMemberValueType(), items[ i ].Entry.Contract.NilImplication );
				}
				else
				{
					Emittion.EmitDeserializeValue( emitter, unpackerIL, 1, items[ i ].UnpackedLocal, items[ i ].IsUnpackedLocal, items[ i ].Entry.Member.Name, items[ i ].Entry.Contract.NilImplication, null );
				}

				// TOOD: Record for missing check

				unpackerIL.EmitBr( whileCond );
				unpackerIL.MarkLabel( endIf0 );
			}

			// TODO: Handle unknown member.

			unpackerIL.MarkLabel( endWhile );

			// TOOD: Check missing

			foreach ( var item in items )
			{
				if ( item.UnpackedLocal == null )
				{
					continue;
				}

				// TODO: Mandatory -> Exception?
				var endIf = unpackerIL.DefineLabel( "END_IF" );
				unpackerIL.EmitAnyLdloc( item.IsUnpackedLocal );
				unpackerIL.EmitBrfalse_S( endIf );

				if ( result.LocalType.IsValueType )
				{
					unpackerIL.EmitAnyLdloca( result );
				}
				else
				{
					unpackerIL.EmitAnyLdloc( result );
				}

				unpackerIL.EmitAnyLdloc( item.UnpackedLocal );
				Emittion.EmitStoreValue( unpackerIL, item.Entry.Member );
				unpackerIL.MarkLabel( endIf );
			}
		}

		/// <summary>
		///		Creates serializer as <typeparamref name="TObject"/> is array type.
		/// </summary>
		/// <returns>
		///		<see cref="MessagePackSerializer{T}"/>. 
		///		This value will not be <c>null</c>.
		/// </returns>
		public sealed override MessagePackSerializer<TObject> CreateArraySerializer()
		{
			using ( var emitter = EmittingSerializerBuilderLogics.CreateArraySerializerCore( typeof( TObject ), this._emitterFlavor ) )
			{
				try
				{
					return emitter.CreateInstance<TObject>( this.Context );
				}
				finally
				{
					emitter.FlushTrace();
				}
			}
		}

		/// <summary>
		///		Creates serializer as <typeparamref name="TObject"/> is map type.
		/// </summary>
		/// <returns>
		///		<see cref="MessagePackSerializer{T}"/>. 
		///		This value will not be <c>null</c>.
		/// </returns>
		public sealed override MessagePackSerializer<TObject> CreateMapSerializer()
		{
			using ( var emitter = EmittingSerializerBuilderLogics.CreateMapSerializerCore( typeof( TObject ), this._emitterFlavor ) )
			{
				try
				{
					return emitter.CreateInstance<TObject>( this.Context );
				}
				finally
				{
					emitter.FlushTrace();
				}
			}
		}

		/// <summary>
		///		Creates serializer as <typeparamref name="TObject"/> is tuple type.
		/// </summary>
		/// <returns>
		///		<see cref="MessagePackSerializer{T}"/>. 
		///		This value will not be <c>null</c>.
		/// </returns>
		public sealed override MessagePackSerializer<TObject> CreateTupleSerializer()
		{
#if WINDOWS_PHONE
			throw new PlatformNotSupportedException();
#else
			using ( var emitter = EmittingSerializerBuilderLogics.CreateTupleSerializerCore( typeof( TObject ), this._emitterFlavor ) )
			{
				try
				{
					return emitter.CreateInstance<TObject>( this.Context );
				}
				finally
				{
					emitter.FlushTrace();
				}
			}
#endif
		}
	}
}