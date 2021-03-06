﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using dndbg.Engine;
using dnlib.PE;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.Scripting;
using dnSpy.Contracts.Scripting.Debugger;
using dnSpy.Debugger.Modules;

namespace dnSpy.Debugger.Scripting {
	sealed class DebuggerModule : IDebuggerModule {
		public ModuleId ModuleId => moduleId;
		public IAppDomain AppDomain => debugger.Dispatcher.UI(() => new DebuggerAppDomain(debugger, mod.AppDomain));
		public IDebuggerAssembly Assembly => debugger.Dispatcher.UI(() => new DebuggerAssembly(debugger, mod.Assembly));

		public string DnlibName {
			get {
				if (dnlibName != null)
					return dnlibName;
				debugger.Dispatcher.UI(() => {
					if (dnlibName == null)
						dnlibName = mod.DnlibName;
				});
				return dnlibName;
			}
		}
		string dnlibName;

		public bool HasUnloaded => debugger.Dispatcher.UI(() => mod.HasUnloaded);
		public int UniqueId => uniqueId;
		public bool IsDynamic => moduleId.IsDynamic;
		public bool IsInMemory => moduleId.IsInMemory;
		public bool IsManifestModule => debugger.Dispatcher.UI(() => mod.CorModule.IsManifestModule);
		public string Name => name;
		public string UniquerName => debugger.Dispatcher.UI(() => mod.CorModule.UniquerName);
		public ulong Address => address;
		public uint Size => size;

		readonly Debugger debugger;
		readonly DnModule mod;
		readonly int hashCode;
		readonly int uniqueId;
		readonly ulong address;
		readonly uint size;
		readonly string name;
		/*readonly*/ ModuleId moduleId;

		public DebuggerModule(Debugger debugger, DnModule mod) {
			debugger.Dispatcher.VerifyAccess();
			this.debugger = debugger;
			this.mod = mod;
			this.hashCode = mod.GetHashCode();
			this.uniqueId = mod.UniqueId;
			this.name = mod.Name;
			this.address = mod.Address;
			this.size = mod.Size;
			var moduleId = mod.DnModuleId;
			this.moduleId = new ModuleId(moduleId.AssemblyFullName, moduleId.ModuleName, moduleId.IsDynamic, moduleId.IsInMemory, moduleId.ModuleNameOnly);
		}

		public IDebuggerAssembly ResolveAssembly(uint asmRefToken) => debugger.Dispatcher.UI(() => {
			var corAsm = mod.CorModule.ResolveAssembly(asmRefToken);
			if (corAsm == null)
				return null;
			var asm = mod.AppDomain.Assemblies.FirstOrDefault(a => a.CorAssembly == corAsm);
			return asm == null ? null : new DebuggerAssembly(debugger, asm);
		});

		public IDebuggerMethod GetMethod(uint token) => debugger.Dispatcher.UI(() => {
			var func = mod.CorModule.GetFunctionFromToken(token);
			return func == null ? null : new DebuggerMethod(debugger, func);
		});

		public IDebuggerField GetField(uint token) => debugger.Dispatcher.UI(() => {
			var field = mod.CorModule.GetFieldFromToken(token);
			return field == null ? null : new DebuggerField(debugger, field);
		});

		public IDebuggerProperty GetProperty(uint token) => debugger.Dispatcher.UI(() => {
			var prop = mod.CorModule.GetPropertyFromToken(token);
			return prop == null ? null : new DebuggerProperty(debugger, prop);
		});

		public IDebuggerEvent GetEvent(uint token) => debugger.Dispatcher.UI(() => {
			var evt = mod.CorModule.GetEventFromToken(token);
			return evt == null ? null : new DebuggerEvent(debugger, evt);
		});

		public IDebuggerClass GetClass(uint token) => debugger.Dispatcher.UI(() => {
			var cls = mod.CorModule.GetClassFromToken(token);
			return cls == null ? null : new DebuggerClass(debugger, cls);
		});

		public IDebuggerType GetType(uint token) => debugger.Dispatcher.UI(() => {
			var cls = mod.CorModule.GetClassFromToken(token);
			return cls == null ? null : new DebuggerClass(debugger, cls).ToType(Array.Empty<IDebuggerType>());
		});

		public IDebuggerValue GetGlobalVariableValue(uint fdToken) => debugger.Dispatcher.UI(() => {
			var value = mod.CorModule.GetGlobalVariableValue(fdToken);
			return value == null ? null : new DebuggerValue(debugger, value);
		});

		public void SetJMCStatus(bool isJustMyCode) => debugger.Dispatcher.UI(() => mod.CorModule.SetJMCStatus(isJustMyCode));

		sealed class PEState {
			public static readonly PEState Null = new PEState();

			public readonly ImageSectionHeader[] Sections;

			PEState() {
				this.Sections = Array.Empty<ImageSectionHeader>();
			}

			public PEState(ImageSectionHeader[] sections) {
				this.Sections = sections;
			}
		}
		PEState peState;

		public void InitializePE() {
			if (peState == null)
				Interlocked.CompareExchange(ref peState, CreatePEState(), null);
		}

		PEState CreatePEState() {
			try {
				ulong addr = Address;
				if (addr == 0)
					return PEState.Null;
				var data = new byte[0x1000];
				debugger.Read(Address, data, 0, data.Length);
				using (var peImage = new PEImage(data, IsFileLayout ? ImageLayout.File : ImageLayout.Memory, true))
					return new PEState(peImage.ImageSectionHeaders.ToArray());
			}
			catch {
				Debug.Fail("Couldn't read section headers");
				return PEState.Null;
			}
		}

		public bool IsFileLayout => !IsDynamic && IsInMemory;
		public bool IsMemoryLayout => !IsDynamic && !IsInMemory;

		public ulong RVAToAddress(uint rva) {
			if (IsFileLayout)
				return Address + RVAToOffset(rva);
			return Address + rva;
		}

		public ulong OffsetToAddress(uint offset) {
			if (IsFileLayout)
				return Address + offset;
			return Address + OffsetToRVA(offset);
		}

		public uint AddressToRVA(ulong address) {
			if (Address == 0 || Size == 0)
				return uint.MaxValue;
			if (address < Address || address > Address + Size - 1)
				return uint.MaxValue;
			if (IsFileLayout)
				return OffsetToRVA((uint)(address - Address));
			return (uint)(address - Address);
		}

		public uint AddressToOffset(ulong address) {
			if (Address == 0 || Size == 0)
				return uint.MaxValue;
			if (address < Address || address > Address + Size - 1)
				return uint.MaxValue;
			if (IsFileLayout)
				return (uint)(address - Address);
			return RVAToOffset((uint)(address - Address));
		}

		public uint RVAToOffset(uint rva) {
			if (peState == null)
				InitializePE();

			foreach (var sect in peState.Sections) {
				if ((uint)sect.VirtualAddress <= rva && rva < (uint)sect.VirtualAddress + Math.Max(sect.SizeOfRawData, sect.VirtualSize))
					return rva - (uint)sect.VirtualAddress + sect.PointerToRawData;
			}
			return rva;
		}

		public uint OffsetToRVA(uint offset) {
			if (peState == null)
				InitializePE();

			foreach (var sect in peState.Sections) {
				if (sect.PointerToRawData <= offset && offset < sect.PointerToRawData + sect.SizeOfRawData)
					return offset - sect.PointerToRawData + (uint)sect.VirtualAddress;
			}
			return offset;
		}

		public void Read(uint rva, byte[] array, long index, uint count) => debugger.Read(RVAToAddress(rva), array, index, count);
		public void Read(uint rva, byte[] array, long index, int count) => debugger.Read(RVAToAddress(rva), array, index, count);
		public byte[] Read(uint rva, uint count) => debugger.Read(RVAToAddress(rva), count);
		public byte[] Read(uint rva, int count) => debugger.Read(RVAToAddress(rva), count);
		public uint Write(uint rva, byte[] array, long index, uint count) => debugger.Write(RVAToAddress(rva), array, index, count);
		public int Write(uint rva, byte[] array, long index, int count) => debugger.Write(RVAToAddress(rva), array, index, count);
		public void Write(uint rva, byte[] array) => debugger.Write(RVAToAddress(rva), array);
		public bool ReadBoolean(uint rva) => debugger.ReadBoolean(RVAToAddress(rva));
		public char ReadChar(uint rva) => debugger.ReadChar(RVAToAddress(rva));
		public sbyte ReadSByte(uint rva) => debugger.ReadSByte(RVAToAddress(rva));
		public byte ReadByte(uint rva) => debugger.ReadByte(RVAToAddress(rva));
		public short ReadInt16(uint rva) => debugger.ReadInt16(RVAToAddress(rva));
		public ushort ReadUInt16(uint rva) => debugger.ReadUInt16(RVAToAddress(rva));
		public int ReadInt32(uint rva) => debugger.ReadInt32(RVAToAddress(rva));
		public uint ReadUInt32(uint rva) => debugger.ReadUInt32(RVAToAddress(rva));
		public long ReadInt64(uint rva) => debugger.ReadInt64(RVAToAddress(rva));
		public ulong ReadUInt64(uint rva) => debugger.ReadUInt64(RVAToAddress(rva));
		public float ReadSingle(uint rva) => debugger.ReadSingle(RVAToAddress(rva));
		public double ReadDouble(uint rva) => debugger.ReadDouble(RVAToAddress(rva));
		public decimal ReadDecimal(uint rva) => debugger.ReadDecimal(RVAToAddress(rva));
		public void Write(uint rva, bool value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, char value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, sbyte value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, byte value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, short value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, ushort value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, int value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, uint value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, long value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, ulong value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, float value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, double value) => debugger.Write(RVAToAddress(rva), value);
		public void Write(uint rva, decimal value) => debugger.Write(RVAToAddress(rva), value);
		public void ReadOffset(uint offset, byte[] array, long index, uint count) => debugger.Read(OffsetToAddress(offset), array, index, count);
		public void ReadOffset(uint offset, byte[] array, long index, int count) => debugger.Read(OffsetToAddress(offset), array, index, count);
		public byte[] ReadOffset(uint offset, uint count) => debugger.Read(OffsetToAddress(offset), count);
		public byte[] ReadOffset(uint offset, int count) => debugger.Read(OffsetToAddress(offset), count);
		public uint WriteOffset(uint offset, byte[] array, long index, uint count) => debugger.Write(OffsetToAddress(offset), array, index, count);
		public int WriteOffset(uint offset, byte[] array, long index, int count) => debugger.Write(OffsetToAddress(offset), array, index, count);
		public void WriteOffset(uint offset, byte[] array) => debugger.Write(OffsetToAddress(offset), array);
		public bool ReadBooleanOffset(uint offset) => debugger.ReadBoolean(OffsetToAddress(offset));
		public char ReadCharOffset(uint offset) => debugger.ReadChar(OffsetToAddress(offset));
		public sbyte ReadSByteOffset(uint offset) => debugger.ReadSByte(OffsetToAddress(offset));
		public byte ReadByteOffset(uint offset) => debugger.ReadByte(OffsetToAddress(offset));
		public short ReadInt16Offset(uint offset) => debugger.ReadInt16(OffsetToAddress(offset));
		public ushort ReadUInt16Offset(uint offset) => debugger.ReadUInt16(OffsetToAddress(offset));
		public int ReadInt32Offset(uint offset) => debugger.ReadInt32(OffsetToAddress(offset));
		public uint ReadUInt32Offset(uint offset) => debugger.ReadUInt32(OffsetToAddress(offset));
		public long ReadInt64Offset(uint offset) => debugger.ReadInt64(OffsetToAddress(offset));
		public ulong ReadUInt64Offset(uint offset) => debugger.ReadUInt64(OffsetToAddress(offset));
		public float ReadSingleOffset(uint offset) => debugger.ReadSingle(OffsetToAddress(offset));
		public double ReadDoubleOffset(uint offset) => debugger.ReadDouble(OffsetToAddress(offset));
		public decimal ReadDecimalOffset(uint offset) => debugger.ReadDecimal(OffsetToAddress(offset));
		public void WriteOffset(uint offset, bool value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, char value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, sbyte value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, byte value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, short value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, ushort value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, int value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, uint value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, long value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, ulong value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, float value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, double value) => debugger.Write(OffsetToAddress(offset), value);
		public void WriteOffset(uint offset, decimal value) => debugger.Write(OffsetToAddress(offset), value);

		public byte[] Save() {
			if (Address == 0 || Size == 0)
				throw new InvalidOperationException("Can't save a dynamic assembly");
			//TODO: This code allocates 3 arrays
			var allBytes = Read(0, Size);
			if (IsInMemory)
				return allBytes;
			var bytes = new byte[Size];
			int finalSize;
			PEFilesSaver.WritePEFile(allBytes, bytes, allBytes.Length, out finalSize);
			if (finalSize == bytes.Length)
				return bytes;
			var final = new byte[finalSize];
			Array.Copy(bytes, 0, final, 0, finalSize);
			return final;
		}

		public void Save(Stream stream) {
			var bytes = Save();
			stream.Write(bytes, 0, bytes.Length);
		}

		public void Save(string filename) {
			using (var stream = File.Create(filename))
				Save(stream);
		}

		public IDebuggerClass GetClass(string className) => debugger.Dispatcher.UI(() => {
			// Dynamic modules can get extra types, so use the slower linear search.
			var cls = IsDynamic ?
					mod.CorModule.FindClass(className) :
					mod.CorModule.FindClassCache(className);
			return cls == null ? null : new DebuggerClass(debugger, cls);
		});

		public IDebuggerMethod GetMethod(string className, string methodName) => debugger.Dispatcher.UI(() => GetClass(className)?.GetMethod(methodName));
		public IDebuggerField GetField(string className, string fieldName) => debugger.Dispatcher.UI(() => GetClass(className)?.GetField(fieldName));
		public IDebuggerProperty GetProperty(string className, string propertyName) => debugger.Dispatcher.UI(() => GetClass(className)?.GetProperty(propertyName));
		public IDebuggerEvent GetEvent(string className, string eventName) => debugger.Dispatcher.UI(() => GetClass(className)?.GetEvent(eventName));
		public IDebuggerType GetType(string className) => GetType(className, null);

		public IDebuggerType GetType(string className, params IDebuggerType[] genericArguments) => debugger.Dispatcher.UI(() => {
			var cls = (DebuggerClass)GetClass(className);
			if (cls == null)
				return null;
			// We can use Class all the time, even for value types
			var type = cls.CorClass.GetParameterizedType(dndbg.COM.CorDebug.CorElementType.Class, genericArguments.ToCorTypes());
			Debug.Assert(type != null);
			return type == null ? null : new DebuggerType(debugger, type, cls.Token);
		});

		public IDebuggerType GetType(Type type) => debugger.Dispatcher.UI(() => debugger.FindAppDomainUI(mod.AppDomain.CorAppDomain)?.GetType(type));
		public IDebuggerField GetField(FieldInfo field) => debugger.Dispatcher.UI(() => GetType(field.DeclaringType)?.GetField(field));
		public IDebuggerMethod GetMethod(MethodBase method) => debugger.Dispatcher.UI(() => GetType(method.DeclaringType)?.GetMethod(method));
		public IDebuggerProperty GetProperty(PropertyInfo prop) => debugger.Dispatcher.UI(() => GetType(prop.DeclaringType)?.GetProperty(prop));
		public IDebuggerEvent GetEvent(EventInfo evt) => debugger.Dispatcher.UI(() => GetType(evt.DeclaringType)?.GetEvent(evt));
		public override bool Equals(object obj) => (obj as DebuggerModule)?.mod == mod;
		public override int GetHashCode() => hashCode;
		public override string ToString() => debugger.Dispatcher.UI(() => mod.ToString());
	}
}
