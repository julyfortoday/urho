﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Perception.Spatial;
using Windows.Perception.Spatial.Surfaces;
using Urho.Holographics;

namespace Urho
{
	public class SpatialMappingManager
	{
		SpatialSurfaceObserver observer;
		SpatialCoordinateSystem currentCoordinateSystem;
		Dictionary<Guid, DateTime> updateCache = new Dictionary<Guid, DateTime>();
		HoloApplication currentHoloApp;
		int trianglesPerCubicMeter;

		SpatialSurfaceMeshOptions options = new SpatialSurfaceMeshOptions
			{
				//TODO: check if supported?
				VertexPositionFormat = DirectXPixelFormat.R16G16B16A16IntNormalized,
				VertexNormalFormat = DirectXPixelFormat.R8G8B8A8IntNormalized,
				TriangleIndexFormat = DirectXPixelFormat.R16UInt,
				IncludeVertexNormals = true,
			};

		public async Task<bool> Register(HoloApplication app, SpatialCoordinateSystem coordinateSystem, System.Numerics.Vector3 extents, int trianglesPerCubicMeter = 1000)
		{
			this.currentHoloApp = app;
			this.trianglesPerCubicMeter = trianglesPerCubicMeter;
			this.currentCoordinateSystem = coordinateSystem;

			var result = await SpatialSurfaceObserver.RequestAccessAsync();
			if (result != SpatialPerceptionAccessStatus.Allowed)
				return false;

			observer = new SpatialSurfaceObserver();
			observer.SetBoundingVolume(SpatialBoundingVolume.FromBox(coordinateSystem, new SpatialBoundingBox { Extents = extents }));
			foreach (var surface in observer.GetObservedSurfaces())
			{
				updateCache[surface.Key] = surface.Value.UpdateTime.UtcDateTime;
				ProcessSurface(surface.Value);
			}
			observer.ObservedSurfacesChanged += Observer_ObservedSurfacesChanged;

			return true;
		}

		public void Stop()
		{
			if (observer != null)
				observer.ObservedSurfacesChanged -= Observer_ObservedSurfacesChanged;
			observer = null;
		}

		void Observer_ObservedSurfacesChanged(SpatialSurfaceObserver sender, object args)
		{
			var surfaces = sender.GetObservedSurfaces();
			Application.InvokeOnMain(() => currentHoloApp.HandleActiveSurfacesChanged(new HashSet<string>(surfaces.Keys.Select(k => k.ToString()))));
			foreach (var surface in surfaces.Values)
			{
				lock (updateCache)
				{
					DateTime updateTime;
					if (updateCache.TryGetValue(surface.Id, out updateTime) && updateTime >= surface.UpdateTime.UtcDateTime)
						return;
					updateCache[surface.Id] = surface.UpdateTime.UtcDateTime;
				}
				if (observer == null)
					return;
				ProcessSurface(surface);
			}
		}

		async Task ProcessSurface(SpatialSurfaceInfo surface)
		{
			var mesh = await surface.TryComputeLatestMeshAsync(trianglesPerCubicMeter, options);
			if (observer == null)
				return;

			var bounds = mesh.SurfaceInfo.TryGetBounds(currentCoordinateSystem);
			if (bounds == null)
				return;

			//1. TriangleIndices
			var trianglesBytes = mesh.TriangleIndices.Data.ToArray();
			var indeces = new short[mesh.TriangleIndices.ElementCount];
			int indexOffset = 0;
			for (int i = 0; i < mesh.TriangleIndices.ElementCount; i++)
			{
				//DirectXPixelFormat.R16UInt
				var index = BitConverter.ToInt16(trianglesBytes, indexOffset);
				indexOffset += 2;
				indeces[i] = index;
			}

			var vertexCount = mesh.VertexPositions.ElementCount;
			var vertexRawData = mesh.VertexPositions.Data.ToArray();
			var vertexScale = mesh.VertexPositionScale;
			var normalsRawData = mesh.VertexNormals.Data.ToArray();

			float[] vertexData = new float[vertexCount * 6];
			//2,3 - VertexPositions and Normals
			for (int i = 0; i < vertexCount; i++)
			{
				//VertexPositions: DirectXPixelFormat.R16G16B16A16IntNormalized
				short x = BitConverter.ToInt16(vertexRawData, i * 8 + 0);
				short y = BitConverter.ToInt16(vertexRawData, i * 8 + 2);
				short z = BitConverter.ToInt16(vertexRawData, i * 8 + 4);
				//omit w

				//short to float:
				float xx = (x == -32768) ? -1.0f : x * 1.0f / (32767.0f);
				float yy = (y == -32768) ? -1.0f : y * 1.0f / (32767.0f);
				float zz = (z == -32768) ? -1.0f : z * 1.0f / (32767.0f);

				//Normals: DirectXPixelFormat.R8G8B8A8IntNormalized
				var normalX = normalsRawData[i * 4 + 0];
				var normalY = normalsRawData[i * 4 + 1];
				var normalZ = normalsRawData[i * 4 + 2];
				//omit w

				//merge vertex+normals for Urho3D (also, change RH to LH coordinate systems)
				vertexData[i * 6 + 0] = xx * vertexScale.X;
				vertexData[i * 6 + 1] = yy * vertexScale.Y;
				vertexData[i * 6 + 2] = -zz * vertexScale.Z;
				vertexData[i * 6 + 3] = normalX == 0 ? 0 : 255 / normalX;
				vertexData[i * 6 + 4] = normalY == 0 ? 0 : 255 / normalY;
				vertexData[i * 6 + 5] = normalZ == 0 ? 0 : -255 / normalZ;
			}

			var boundsRotation = new Quaternion(-bounds.Value.Orientation.X, -bounds.Value.Orientation.Y, bounds.Value.Orientation.Z, bounds.Value.Orientation.W);
			var boundsCenter = new Vector3(bounds.Value.Center.X, bounds.Value.Center.Y, -bounds.Value.Center.Z);

			Application.InvokeOnMain(() => currentHoloApp.HandleSurfaceUpdated(surface.Id.ToString(), surface.UpdateTime, vertexData, indeces, boundsCenter, boundsRotation));
		}
	}
}
