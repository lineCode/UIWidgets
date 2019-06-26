﻿using System;
using System.Collections.Generic;
using Unity.UIWidgets.foundation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.UIWidgets.ui {
    public partial class PictureFlusher {
        readonly RenderTexture _renderTexture;
        readonly float _fringeWidth;
        readonly float _devicePixelRatio;
        readonly MeshPool _meshPool;

        readonly List<RenderLayer> _layers = new List<RenderLayer>();
        RenderLayer _currentLayer;
        uiRect? _lastScissor;

        public void dispose() {
            if (this._currentLayer != null) {
                this._clearLayer(this._currentLayer);
                this._currentLayer.dispose();
                this._currentLayer = null;
                this._lastScissor = null;
                this._layers.Clear();
            }
        }

        public PictureFlusher(RenderTexture renderTexture, float devicePixelRatio, MeshPool meshPool) {
            D.assert(renderTexture);
            D.assert(devicePixelRatio > 0);
            D.assert(meshPool != null);

            this._renderTexture = renderTexture;
            this._fringeWidth = 1.0f / devicePixelRatio;
            this._devicePixelRatio = devicePixelRatio;
            this._meshPool = meshPool;
        }

        public float getDevicePixelRatio() {
            return this._devicePixelRatio;
        }

        void _reset() {
            //clear all states
            D.assert(this._layers.Count == 0 || (this._layers.Count == 1 && this._layers[0] == this._currentLayer));
            if (this._currentLayer != null) {
                this._clearLayer(this._currentLayer);
                this._currentLayer.dispose();
                this._currentLayer = null;
                this._lastScissor = null;
                this._layers.Clear();
            }

            var width = this._renderTexture.width;
            var height = this._renderTexture.height;

            var bounds = uiRectHelper.fromLTWH(0, 0,
                width * this._fringeWidth,
                height * this._fringeWidth);

            RenderLayer firstLayer = RenderLayer.create(
                width: width,
                height: height,
                layerBounds: bounds
            );

            this._layers.Add(firstLayer);
            this._currentLayer = firstLayer;
        }

        void _save() {
            var layer = this._currentLayer;
            layer.currentState = layer.currentState.copy();
            layer.states.Add(layer.currentState);
            layer.clipStack.save();
        }

        readonly uiOffset[] _saveLayer_Points = new uiOffset[4];

        void _saveLayer(uiRect bounds, Paint paint) {
            D.assert(bounds.width > 0);
            D.assert(bounds.height > 0);
            D.assert(paint != null);

            var parentLayer = this._currentLayer;
            var state = parentLayer.currentState;
            var textureWidth = Mathf.CeilToInt(
                bounds.width * state.scale * this._devicePixelRatio);
            if (textureWidth < 1) {
                textureWidth = 1;
            }

            var textureHeight = Mathf.CeilToInt(
                bounds.height * state.scale * this._devicePixelRatio);
            if (textureHeight < 1) {
                textureHeight = 1;
            }

            var layer = RenderLayer.create(
                rtID: Shader.PropertyToID("_rtID_" + this._layers.Count + "_" + parentLayer.layers.Count),
                width: textureWidth,
                height: textureHeight,
                layerBounds: bounds,
                layerPaint: paint
            );

            parentLayer.addLayer(layer);
            this._layers.Add(layer);
            this._currentLayer = layer;

            if (paint.backdrop != null) {
                if (paint.backdrop is _BlurImageFilter) {
                    var filter = (_BlurImageFilter) paint.backdrop;
                    if (!(filter.sigmaX == 0 && filter.sigmaY == 0)) {
                        this._saveLayer_Points[0] = bounds.topLeft;
                        this._saveLayer_Points[1] = bounds.bottomLeft;
                        this._saveLayer_Points[2] = bounds.bottomRight;
                        this._saveLayer_Points[3] = bounds.topRight;
                        
                        state.matrix.Value.mapPoints(this._saveLayer_Points);

                        var parentBounds = parentLayer.layerBounds;
                        for (int i = 0; i < 4; i++) {
                            this._saveLayer_Points[i] = new uiOffset(
                                (this._saveLayer_Points[i].dx - parentBounds.left) / parentBounds.width,
                                (this._saveLayer_Points[i].dy - parentBounds.top) / parentBounds.height
                            );
                        }

                        var mesh = ImageMeshGenerator.imageMesh(
                            null,
                            this._saveLayer_Points[0], 
                            this._saveLayer_Points[1], 
                            this._saveLayer_Points[2], 
                            this._saveLayer_Points[3],
                            bounds);
                        var renderDraw = CanvasShader.texRT(layer, layer.layerPaint, mesh, parentLayer);
                        layer.draws.Add(renderDraw);

                        var blurLayer = this._createBlurLayer(layer, filter.sigmaX, filter.sigmaY, layer);
                        var blurMesh = ImageMeshGenerator.imageMesh(null, uiRectHelper.one, bounds);
                        layer.draws.Add(CanvasShader.texRT(layer, paint, blurMesh, blurLayer));
                    }
                }
                else if (paint.backdrop is _MatrixImageFilter) {
                    var filter = (_MatrixImageFilter) paint.backdrop;
                    if (!filter.transform.isIdentity()) {
                        layer.filterMode = filter.filterMode;
                        
                        this._saveLayer_Points[0] = bounds.topLeft;
                        this._saveLayer_Points[1] = bounds.bottomLeft;
                        this._saveLayer_Points[2] = bounds.bottomRight;
                        this._saveLayer_Points[3] = bounds.topRight;
                        state.matrix.Value.mapPoints(this._saveLayer_Points);

                        var parentBounds = parentLayer.layerBounds;
                        for (int i = 0; i < 4; i++) {
                            this._saveLayer_Points[i] = new uiOffset(
                                (this._saveLayer_Points[i].dx - parentBounds.left) / parentBounds.width,
                                (this._saveLayer_Points[i].dy - parentBounds.top) / parentBounds.height
                            );
                        }

                        var matrix = uiMatrix3.makeTrans(-bounds.left, -bounds.top);
                        matrix.postConcat(uiMatrix3.fromMatrix3(filter.transform));
                        matrix.postTranslate(bounds.left, bounds.top);

                        var mesh = ImageMeshGenerator.imageMesh(
                            matrix,
                            this._saveLayer_Points[0], 
                            this._saveLayer_Points[1], 
                            this._saveLayer_Points[2], 
                            this._saveLayer_Points[3],
                            bounds);
                        var renderDraw = CanvasShader.texRT(layer, layer.layerPaint, mesh, parentLayer);
                        layer.draws.Add(renderDraw);
                    }
                }
            }
        }

        void _restore() {
            var layer = this._currentLayer;
            D.assert(layer.states.Count > 0);
            if (layer.states.Count > 1) {
                layer.states[layer.states.Count - 1].dispose();
                layer.states.RemoveAt(layer.states.Count - 1);
                layer.currentState = layer.states[layer.states.Count - 1];
                layer.clipStack.restore();
                return;
            }

            this._layers.RemoveAt(this._layers.Count - 1);
            var currentLayer = this._currentLayer = this._layers[this._layers.Count - 1];
            var state = currentLayer.currentState;

            var mesh = ImageMeshGenerator.imageMesh(state.matrix, uiRectHelper.one, layer.layerBounds);

            if (!this._applyClip(mesh.bounds)) {
                mesh.dispose();
                return;
            }

            var renderDraw = CanvasShader.texRT(currentLayer, layer.layerPaint, mesh, layer);
            currentLayer.draws.Add(renderDraw);
        }

        void _translate(float dx, float dy) {
            var state = this._currentLayer.currentState;
            var matrix = uiMatrix3.makeTrans(dx, dy);
            matrix.postConcat(state.matrix.Value);
            state.matrix = matrix;
        }

        void _scale(float sx, float? sy = null) {
            var state = this._currentLayer.currentState;
            var matrix = uiMatrix3.makeScale(sx, (sy ?? sx));
            matrix.postConcat(state.matrix.Value);
            state.matrix = matrix;
        }

        void _rotate(float radians, uiOffset? offset = null) {
            var state = this._currentLayer.currentState;
            if (offset == null) {
                var matrix = uiMatrix3.makeRotate(radians);
                matrix.postConcat(state.matrix.Value);
                state.matrix = matrix;
            }
            else {
                var matrix = uiMatrix3.makeRotate(radians, offset.Value.dx, offset.Value.dy);
                matrix.postConcat(state.matrix.Value);
                state.matrix = matrix;
            }
        }

        void _skew(float sx, float sy) {
            var state = this._currentLayer.currentState;
            var matrix = uiMatrix3.makeSkew(sx, sy);
            matrix.postConcat(state.matrix.Value);
            state.matrix = matrix;
        }

        void _concat(uiMatrix3 matrix) {
            var state = this._currentLayer.currentState;
            matrix = new uiMatrix3(matrix);
            matrix.postConcat(state.matrix.Value);
            state.matrix = matrix;
        }

        void _resetMatrix() {
            var state = this._currentLayer.currentState;
            state.matrix = uiMatrix3.I();
        }

        void _setMatrix(uiMatrix3 matrix) {
            var state = this._currentLayer.currentState;
            state.matrix = new uiMatrix3(matrix);
        }

        void _clipRect(Rect rect) {
            var path = uiPath.create();
            path.addRect(uiRectHelper.fromRect(rect));
            this._clipPath(path);
            path.dispose();
        }
        
        void _clipUIRect(uiRect rect) {
            var path = uiPath.create();
            path.addRect(rect);
            this._clipPath(path);
            path.dispose();
        }

        void _clipRRect(RRect rrect) {
            var path = uiPath.create();
            path.addRRect(rrect);
            this._clipPath(path);
            path.dispose();
        }

        void _clipPath(uiPath path) {
            var layer = this._currentLayer;
            var state = layer.currentState;
            layer.clipStack.clipPath(path, state.matrix.Value, state.scale * this._devicePixelRatio);
        }

        void _tryAddScissor(RenderLayer layer, uiRect? scissor) {
            if (uiRectHelper.equals(scissor, this._lastScissor)) {
                return;
            }

            layer.draws.Add(CmdScissor.create(
                deviceScissor: scissor
            ));
            this._lastScissor = scissor;
        }

        bool _applyClip(uiRect? queryBounds) {
            if (queryBounds == null || queryBounds.Value.isEmpty) {
                return false;
            }

            var layer = this._currentLayer;
            var layerBounds = layer.layerBounds;
            ReducedClip reducedClip = ReducedClip.create(layer.clipStack, layerBounds, queryBounds.Value);
            if (reducedClip.isEmpty()) {
                reducedClip.dispose();
                return false;
            }

            var scissor = reducedClip.scissor;
            var physicalRect = uiRectHelper.fromLTRB(0, 0, layer.width, layer.height);

            if (uiRectHelper.equals(scissor,layerBounds)) {
                this._tryAddScissor(layer, null);
            }
            else {
                var deviceScissor = uiRectHelper.fromLTRB(
                    scissor.Value.left - layerBounds.left, layerBounds.bottom - scissor.Value.bottom,
                    scissor.Value.right - layerBounds.left, layerBounds.bottom - scissor.Value.top
                );
                deviceScissor = uiRectHelper.scale(deviceScissor, layer.width / layerBounds.width, layer.height / layerBounds.height);
                deviceScissor = uiRectHelper.roundOut(deviceScissor);
                deviceScissor = uiRectHelper.intersect(deviceScissor, physicalRect);

                if (deviceScissor.isEmpty) {
                    reducedClip.dispose();
                    return false;
                }

                this._tryAddScissor(layer, deviceScissor);
            }

            var maskGenID = reducedClip.maskGenID();
            if (this._mustRenderClip(maskGenID, reducedClip.scissor.Value)) {
                if (maskGenID == ClipStack.wideOpenGenID) {
                    layer.ignoreClip = true;
                }
                else {
                    layer.ignoreClip = false;

                    // need to inflate a bit to make sure all area is cleared.
                    var inflatedScissor = uiRectHelper.inflate(reducedClip.scissor.Value, this._fringeWidth);
                    var boundsMesh = uiMeshMesh.create(inflatedScissor);
                    layer.draws.Add(CanvasShader.stencilClear(layer, boundsMesh));

                    foreach (var maskElement in reducedClip.maskElements) {
                        layer.draws.Add(CanvasShader.stencil0(layer, maskElement.mesh.duplicate()));
                        layer.draws.Add(CanvasShader.stencil1(layer, boundsMesh.duplicate()));
                    }
                }

                this._setLastClipGenId(maskGenID, reducedClip.scissor.Value);
            }

            reducedClip.dispose();

            return true;
        }

        void _setLastClipGenId(uint clipGenId, uiRect clipBounds) {
            var layer = this._currentLayer;
            layer.lastClipGenId = clipGenId;
            layer.lastClipBounds = clipBounds;
        }

        bool _mustRenderClip(uint clipGenId, uiRect clipBounds) {
            var layer = this._currentLayer;
            return layer.lastClipGenId != clipGenId || !uiRectHelper.equals(layer.lastClipBounds, clipBounds);
        }

        RenderLayer _createMaskLayer(RenderLayer parentLayer, uiRect maskBounds, _drawPathDrawMeshCallbackDelegate drawCallback,
            Paint paint, bool convex, float alpha, Texture tex, uiRect texBound, TextBlobMesh textMesh, uiMeshMesh mesh) {
            var textureWidth = Mathf.CeilToInt(maskBounds.width * this._devicePixelRatio);
            if (textureWidth < 1) {
                textureWidth = 1;
            }

            var textureHeight = Mathf.CeilToInt(maskBounds.height * this._devicePixelRatio);
            if (textureHeight < 1) {
                textureHeight = 1;
            }

            var maskLayer = RenderLayer.create(
                rtID: Shader.PropertyToID("_rtID_" + this._layers.Count + "_" + parentLayer.layers.Count),
                width: textureWidth,
                height: textureHeight,
                layerBounds: maskBounds,
                filterMode: FilterMode.Bilinear,
                noMSAA: true
            );

            parentLayer.addLayer(maskLayer);
            this._layers.Add(maskLayer);
            this._currentLayer = maskLayer;

            var parentState = parentLayer.states[parentLayer.states.Count - 1];
            var maskState = maskLayer.states[maskLayer.states.Count - 1];
            maskState.matrix = parentState.matrix;

            drawCallback.Invoke(Paint.shapeOnly(paint), mesh, convex, alpha, tex, texBound, textMesh);

            var removed = this._layers.removeLast();
            D.assert(removed == maskLayer);
            this._currentLayer = this._layers[this._layers.Count - 1];

            return maskLayer;
        }

        RenderLayer _createBlurLayer(RenderLayer maskLayer, float sigmaX, float sigmaY, RenderLayer parentLayer) {
            sigmaX = BlurUtils.adjustSigma(sigmaX, out var scaleFactorX, out var radiusX);
            sigmaY = BlurUtils.adjustSigma(sigmaY, out var scaleFactorY, out var radiusY);

            var textureWidth = Mathf.CeilToInt((float) maskLayer.width / scaleFactorX);
            if (textureWidth < 1) {
                textureWidth = 1;
            }

            var textureHeight = Mathf.CeilToInt((float) maskLayer.height / scaleFactorY);
            if (textureHeight < 1) {
                textureHeight = 1;
            }

            var blurXLayer = RenderLayer.create(
                rtID: Shader.PropertyToID("_rtID_" + this._layers.Count + "_" + parentLayer.layers.Count),
                width: textureWidth,
                height: textureHeight,
                layerBounds: maskLayer.layerBounds,
                filterMode: FilterMode.Bilinear,
                noMSAA: true
            );

            parentLayer.addLayer(blurXLayer);

            var blurYLayer = RenderLayer.create(
                rtID: Shader.PropertyToID("_rtID_" + this._layers.Count + "_" + parentLayer.layers.Count),
                width: textureWidth,
                height: textureHeight,
                layerBounds: maskLayer.layerBounds,
                filterMode: FilterMode.Bilinear,
                noMSAA: true
            );

            parentLayer.addLayer(blurYLayer);

            var blurMesh = ImageMeshGenerator.imageMesh(null, uiRectHelper.one, maskLayer.layerBounds);

            var kernelX = BlurUtils.get1DGaussianKernel(sigmaX, radiusX);
            var kernelY = BlurUtils.get1DGaussianKernel(sigmaY, radiusY);

            blurXLayer.draws.Add(CanvasShader.maskFilter(
                blurXLayer, blurMesh, maskLayer,
                radiusX, new Vector2(1f / textureWidth, 0), kernelX));

            blurYLayer.draws.Add(CanvasShader.maskFilter(
                blurYLayer, blurMesh.duplicate(), blurXLayer,
                radiusY, new Vector2(0, -1f / textureHeight), kernelY));

            return blurYLayer;
        }

        void _drawWithMaskFilter(uiRect meshBounds, Paint paint, MaskFilter maskFilter,
            uiMeshMesh mesh, bool convex, float alpha, Texture tex, uiRect texBound, TextBlobMesh textMesh,
            _drawPathDrawMeshCallbackDelegate drawCallback) {
            var layer = this._currentLayer;
            var clipBounds = layer.layerBounds;

            uiRect? stackBounds;
            bool iior;
            layer.clipStack.getBounds(out stackBounds, out iior);

            if (stackBounds != null) {
                clipBounds = uiRectHelper.intersect(clipBounds,stackBounds.Value);
            }

            if (clipBounds.isEmpty) {
                this._drawPathDrawMeshQuit(mesh);
                return;
            }

            var state = layer.currentState;
            float sigma = state.scale * maskFilter.sigma;
            if (sigma <= 0) {
                this._drawPathDrawMeshQuit(mesh);
                return;
            }

            float sigma3 = 3 * sigma;
            var maskBounds = uiRectHelper.inflate(meshBounds, sigma3);
            maskBounds = uiRectHelper.intersect(maskBounds, uiRectHelper.inflate(clipBounds,sigma3));
            if (maskBounds.isEmpty) {
                this._drawPathDrawMeshQuit(mesh);
                return;
            }

            var maskLayer = this._createMaskLayer(layer, maskBounds, drawCallback, paint, convex, alpha, tex, texBound, textMesh, mesh);

            var blurLayer = this._createBlurLayer(maskLayer, sigma, sigma, layer);

            var blurMesh = ImageMeshGenerator.imageMesh(null, uiRectHelper.one, maskBounds);
            if (!this._applyClip(blurMesh.bounds)) {
                blurMesh.dispose();
                this._drawPathDrawMeshQuit(mesh);
                return;
            }

            layer.draws.Add(CanvasShader.texRT(layer, paint, blurMesh, blurLayer));
        }

        delegate void _drawPathDrawMeshCallbackDelegate(Paint p, uiMeshMesh mesh, bool convex, float alpha, Texture tex, uiRect textBlobBounds, TextBlobMesh textMesh);

        void _drawPathDrawMeshCallback(Paint p, uiMeshMesh mesh, bool convex, float alpha, Texture tex, uiRect textBlobBounds, TextBlobMesh textMesh) {
            if (!this._applyClip(mesh.bounds)) {
                mesh.dispose();
                return;
            }

            var layer = this._currentLayer;
            if (convex) {
                layer.draws.Add(CanvasShader.convexFill(layer, p, mesh));
            }
            else {
                layer.draws.Add(CanvasShader.fill0(layer, mesh));
                layer.draws.Add(CanvasShader.fill1(layer, p, mesh.boundsMesh));
            }
        }

        void _drawPathDrawMeshCallback2(Paint p, uiMeshMesh mesh, bool convex, float alpha, Texture tex, uiRect textBlobBounds, TextBlobMesh textMesh) {
            if (!this._applyClip(mesh.bounds)) {
                mesh.dispose();
                return;
            }

            var layer = this._currentLayer;

            layer.draws.Add(CanvasShader.stroke0(layer, p, alpha, mesh));
            layer.draws.Add(CanvasShader.stroke1(layer, mesh.duplicate()));
        }

        void _drawTextDrawMeshCallback(Paint p, uiMeshMesh mesh, bool convex, float alpha, Texture tex, uiRect textBlobBounds, TextBlobMesh textMesh) {
            if (!this._applyClip(textBlobBounds)) {
                textMesh.dispose();
                return;
            }

            var layer = this._currentLayer;
            layer.draws.Add(CanvasShader.texAlpha(layer, p, textMesh, tex));
        }

        void _drawPathDrawMeshQuit(uiMeshMesh mesh) {
            mesh.dispose();
        }

        void _drawPath(uiPath path, Paint paint) {
            D.assert(path != null);
            D.assert(paint != null);

            if (paint.style == PaintingStyle.fill) {
                var state = this._currentLayer.currentState;
                var cache = path.flatten(state.scale * this._devicePixelRatio);

                bool convex;
                var fillMesh = cache.getFillMesh(out convex);
                var mesh = fillMesh.transform(state.matrix);

                if (paint.maskFilter != null && paint.maskFilter.sigma != 0) {
                    this._drawWithMaskFilter(mesh.bounds, paint, paint.maskFilter, mesh, convex, 0, null, uiRectHelper.zero, null, this._drawPathDrawMeshCallback);
                    return;
                }

                this._drawPathDrawMeshCallback(paint, mesh, convex, 0, null, uiRectHelper.zero, null);
            }
            else {
                var state = this._currentLayer.currentState;
                float strokeWidth = (paint.strokeWidth * state.scale).clamp(0, 200.0f);
                float alpha = 1.0f;

                if (strokeWidth == 0) {
                    strokeWidth = this._fringeWidth;
                }
                else if (strokeWidth < this._fringeWidth) {
                    // If the stroke width is less than pixel size, use alpha to emulate coverage.
                    // Since coverage is area, scale by alpha*alpha.
                    alpha = (strokeWidth / this._fringeWidth).clamp(0.0f, 1.0f);
                    alpha *= alpha;
                    strokeWidth = this._fringeWidth;
                }

                var cache = path.flatten(state.scale * this._devicePixelRatio);

                var strokenMesh = cache.getStrokeMesh(
                    strokeWidth / state.scale * 0.5f,
                    paint.strokeCap,
                    paint.strokeJoin,
                    paint.strokeMiterLimit);

                var mesh = strokenMesh.transform(state.matrix);

                if (paint.maskFilter != null && paint.maskFilter.sigma != 0) {
                    this._drawWithMaskFilter(mesh.bounds, paint, paint.maskFilter, mesh, false, alpha, null, uiRectHelper.zero, null, this._drawPathDrawMeshCallback2);
                    return;
                }

                this._drawPathDrawMeshCallback2(paint, mesh, false, alpha, null, uiRectHelper.zero, null);
            }
        }

        void _drawImage(Image image, uiOffset offset, Paint paint) {
            D.assert(image != null);
            D.assert(paint != null);

            this._drawImageRect(image,
                null,
                uiRectHelper.fromLTWH(
                    offset.dx, offset.dy,
                    image.width / this._devicePixelRatio,
                    image.height / this._devicePixelRatio),
                paint);
        }

        void _drawImageRect(Image image, uiRect? src, uiRect dst, Paint paint) {
            D.assert(image != null);
            D.assert(paint != null);

            if (src == null) {
                src = uiRectHelper.one;
            }
            else {
                src = uiRectHelper.scale(src.Value, 1f / image.width, 1f / image.height);
            }

            var layer = this._currentLayer;
            var state = layer.currentState;
            var mesh = ImageMeshGenerator.imageMesh(state.matrix, src.Value, dst);
            if (!this._applyClip(mesh.bounds)) {
                mesh.dispose();
                return;
            }

            layer.draws.Add(CanvasShader.tex(layer, paint, mesh, image));
        }

        void _drawImageNine(Image image, uiRect? src, uiRect center, uiRect dst, Paint paint) {
            D.assert(image != null);
            D.assert(paint != null);

            var scaleX = 1f / image.width;
            var scaleY = 1f / image.height;
            if (src == null) {
                src = uiRectHelper.one;
            }
            else {
                src = uiRectHelper.scale(src.Value, scaleX, scaleY);
            }

            center = uiRectHelper.scale(center, scaleX, scaleY);

            var layer = this._currentLayer;
            var state = layer.currentState;

            var mesh = ImageMeshGenerator.imageNineMesh(state.matrix, src.Value, center, image.width, image.height, dst);
            if (!this._applyClip(mesh.bounds)) {
                mesh.dispose();
                return;
            }

            layer.draws.Add(CanvasShader.tex(layer, paint, mesh, image));
        }
        
        
        void _drawPicture(Picture picture, bool needsSave = true) {
            if (needsSave) {
                this._save();
            }

            int saveCount = 0;

            var drawCmds = picture.drawCmds;
            foreach (var drawCmd in drawCmds) {
                switch (drawCmd) {
                    case DrawSave _:
                        saveCount++;
                        this._save();
                        break;
                    case DrawSaveLayer cmd: {
                        saveCount++;
                        this._saveLayer(uiRectHelper.fromRect(cmd.rect), cmd.paint);
                        break;
                    }
                    case DrawRestore _: {
                        saveCount--;
                        if (saveCount < 0) {
                            throw new Exception("unmatched save/restore in picture");
                        }

                        this._restore();
                        break;
                    }
                    case DrawTranslate cmd: {
                        this._translate(cmd.dx, cmd.dy);
                        break;
                    }
                    case DrawScale cmd: {
                        this._scale(cmd.sx, cmd.sy);
                        break;
                    }
                    case DrawRotate cmd: {
                        this._rotate(cmd.radians, uiOffset.fromOffset(cmd.offset));
                        break;
                    }
                    case DrawSkew cmd: {
                        this._skew(cmd.sx, cmd.sy);
                        break;
                    }
                    case DrawConcat cmd: {
                        this._concat(uiMatrix3.fromMatrix3(cmd.matrix));
                        break;
                    }
                    case DrawResetMatrix _:
                        this._resetMatrix();
                        break;
                    case DrawSetMatrix cmd: {
                        this._setMatrix(uiMatrix3.fromMatrix3(cmd.matrix));
                        break;
                    }
                    case DrawClipRect cmd: {
                        this._clipRect(cmd.rect);
                        break;
                    }
                    case DrawClipRRect cmd: {
                        this._clipRRect(cmd.rrect);
                        break;
                    }
                    case DrawClipPath cmd: {
                        var uipath = uiPath.fromPath(cmd.path);
                        this._clipPath(uipath);
                        uipath.dispose();
                        break;
                    }
                    case DrawPath cmd: {
                        var uipath = uiPath.fromPath(cmd.path);
                        this._drawPath(uipath, cmd.paint);
                        uipath.dispose();
                        break;
                    }
                    case DrawImage cmd: {
                        this._drawImage(cmd.image, (uiOffset.fromOffset(cmd.offset)).Value, cmd.paint);
                        break;
                    }
                    case DrawImageRect cmd: {
                        this._drawImageRect(cmd.image, uiRectHelper.fromRect(cmd.src), uiRectHelper.fromRect(cmd.dst), cmd.paint);
                        break;
                    }
                    case DrawImageNine cmd: {
                        this._drawImageNine(cmd.image, uiRectHelper.fromRect(cmd.src), uiRectHelper.fromRect(cmd.center), uiRectHelper.fromRect(cmd.dst), cmd.paint);
                        break;
                    }
                    case DrawPicture cmd: {
                        this._drawPicture(cmd.picture);
                        break;
                    }
                    case DrawTextBlob cmd: {
                        this._drawTextBlob(cmd.textBlob, (uiOffset.fromOffset(cmd.offset)).Value, cmd.paint);
                        break;
                    }
                    default:
                        throw new Exception("unknown drawCmd: " + drawCmd);
                }
            }

            if (saveCount != 0) {
                throw new Exception("unmatched save/restore in picture");
            }

            if (needsSave) {
                this._restore();
            }
        }
        

        void _drawUIPicture(uiPicture picture, bool needsSave = true) {
            if (needsSave) {
                this._save();
            }

            int saveCount = 0;

            var drawCmds = picture.drawCmds;
            foreach (var drawCmd in drawCmds) {
                switch (drawCmd) {
                    case uiDrawSave _:
                        saveCount++;
                        this._save();
                        break;
                    case uiDrawSaveLayer cmd: {
                        saveCount++;
                        this._saveLayer(cmd.rect.Value, cmd.paint);
                        break;
                    }
                    case uiDrawRestore _: {
                        saveCount--;
                        if (saveCount < 0) {
                            throw new Exception("unmatched save/restore in picture");
                        }

                        this._restore();
                        break;
                    }
                    case uiDrawTranslate cmd: {
                        this._translate(cmd.dx, cmd.dy);
                        break;
                    }
                    case uiDrawScale cmd: {
                        this._scale(cmd.sx, cmd.sy);
                        break;
                    }
                    case uiDrawRotate cmd: {
                        this._rotate(cmd.radians, cmd.offset);
                        break;
                    }
                    case uiDrawSkew cmd: {
                        this._skew(cmd.sx, cmd.sy);
                        break;
                    }
                    case uiDrawConcat cmd: {
                        this._concat(cmd.matrix.Value);
                        break;
                    }
                    case uiDrawResetMatrix _:
                        this._resetMatrix();
                        break;
                    case uiDrawSetMatrix cmd: {
                        this._setMatrix(cmd.matrix.Value);
                        break;
                    }
                    case uiDrawClipRect cmd: {
                        this._clipUIRect(cmd.rect.Value);
                        break;
                    }
                    case uiDrawClipRRect cmd: {
                        this._clipRRect(cmd.rrect);
                        break;
                    }
                    case uiDrawClipPath cmd: {
                        this._clipPath(cmd.path);
                        break;
                    }
                    case uiDrawPath cmd: {
                        this._drawPath(cmd.path, cmd.paint);
                        break;
                    }
                    case uiDrawImage cmd: {
                        this._drawImage(cmd.image, cmd.offset.Value, cmd.paint);
                        break;
                    }
                    case uiDrawImageRect cmd: {
                        this._drawImageRect(cmd.image, cmd.src, cmd.dst.Value, cmd.paint);
                        break;
                    }
                    case uiDrawImageNine cmd: {
                        this._drawImageNine(cmd.image, cmd.src, cmd.center.Value, cmd.dst.Value, cmd.paint);
                        break;
                    }
                    case uiDrawPicture cmd: {
                        this._drawPicture(cmd.picture);
                        break;
                    }
                    case uiDrawTextBlob cmd: {
                        this._drawTextBlob(cmd.textBlob, cmd.offset.Value, cmd.paint);
                        break;
                    }
                    default:
                        throw new Exception("unknown drawCmd: " + drawCmd);
                }
            }

            if (saveCount != 0) {
                throw new Exception("unmatched save/restore in picture");
            }

            if (needsSave) {
                this._restore();
            }
        }

        void _drawTextBlob(TextBlob textBlob, uiOffset offset, Paint paint) {
            D.assert(textBlob != null);
            D.assert(paint != null);

            var state = this._currentLayer.currentState;
            var scale = state.scale * this._devicePixelRatio;

            var matrix = new uiMatrix3(state.matrix.Value);
            matrix.preTranslate(offset.dx, offset.dy);

            var mesh = TextBlobMesh.create(textBlob, scale, matrix);
            var textBlobBounds = matrix.mapRect(uiRectHelper.fromRect(textBlob.boundsInText));

            // request font texture so text mesh could be generated correctly
            var style = textBlob.style;
            var font = FontManager.instance.getOrCreate(style.fontFamily, style.fontWeight, style.fontStyle).font;
            var fontSizeToLoad = Mathf.CeilToInt(style.UnityFontSize * scale);
            var subText = textBlob.text.Substring(textBlob.textOffset, textBlob.textSize);
            font.RequestCharactersInTextureSafe(subText, fontSizeToLoad, style.UnityFontStyle);

            var tex = font.material.mainTexture;

            if (paint.maskFilter != null && paint.maskFilter.sigma != 0) {
                this._drawWithMaskFilter(textBlobBounds, paint, paint.maskFilter, null, false, 0, tex, textBlobBounds, mesh, this._drawTextDrawMeshCallback);
                return;
            }

            this._drawTextDrawMeshCallback(paint, null, false, 0, tex, textBlobBounds, mesh);
        }

        public void flush(uiPicture picture) {
            this._reset();

            this._drawUIPicture(picture, false);

            D.assert(this._layers.Count == 1);
            D.assert(this._layers[0].states.Count == 1);

            var layer = this._currentLayer;
            using (var cmdBuf = new CommandBuffer()) {
                cmdBuf.name = "CommandBufferCanvas";

                this._lastRtID = -1;
                this._drawLayer(layer, cmdBuf);

                // this is necessary for webgl2. not sure why... just to be safe to disable the scissor.
                cmdBuf.DisableScissorRect();

                Graphics.ExecuteCommandBuffer(cmdBuf);
            }
        }

        int _lastRtID;

        void _setRenderTarget(CommandBuffer cmdBuf, int rtID, ref bool toClear) {
            if (this._lastRtID == rtID) {
                return;
            }

            this._lastRtID = rtID;

            if (rtID == 0) {
                cmdBuf.SetRenderTarget(this._renderTexture);
            }
            else {
                cmdBuf.SetRenderTarget(rtID);
            }

            if (toClear) {
                cmdBuf.ClearRenderTarget(true, true, UnityEngine.Color.clear);
                toClear = false;
            }
        }

        readonly float[] _drawLayer_matArray = new float[9];

        void _drawLayer(RenderLayer layer, CommandBuffer cmdBuf) {
            bool toClear = true;

            foreach (var cmdObj in layer.draws) {
                switch (cmdObj) {
                    case CmdLayer cmd:
                        var subLayer = cmd.layer;
                        var desc = new RenderTextureDescriptor(
                            subLayer.width, subLayer.height,
                            RenderTextureFormat.Default, 24) {
                            useMipMap = false,
                            autoGenerateMips = false,
                        };

                        if (this._renderTexture.antiAliasing != 0 && !subLayer.noMSAA) {
                            desc.msaaSamples = this._renderTexture.antiAliasing;
                        }

                        cmdBuf.GetTemporaryRT(subLayer.rtID, desc, subLayer.filterMode);
                        this._drawLayer(subLayer, cmdBuf);

                        break;
                    case CmdDraw cmd:
                        this._setRenderTarget(cmdBuf, layer.rtID, ref toClear);

                        if (cmd.layerId != null) {
                            if (cmd.layerId == 0) {
                                cmdBuf.SetGlobalTexture(CmdDraw.texId, this._renderTexture);
                            }
                            else {
                                cmdBuf.SetGlobalTexture(CmdDraw.texId, cmd.layerId.Value);
                            }
                        }

                        D.assert(cmd.meshObj == null);
                        cmd.meshObj = this._meshPool.getMesh();
                        cmd.meshObjCreated = true;

                        // clear triangles first in order to bypass validation in SetVertices.
                        cmd.meshObj.SetTriangles((int[]) null, 0, false);

                        uiMeshMesh mesh = cmd.mesh;
                        if (cmd.textMesh != null) {
                            mesh = cmd.textMesh.resovleMesh();
                        }

                        if (mesh == null) {
                            continue;
                        }

                        D.assert(mesh.vertices.Count > 0);
                        cmd.meshObj.SetVertices(mesh.vertices?.data);
                        cmd.meshObj.SetTriangles(mesh.triangles?.data, 0, false);
                        cmd.meshObj.SetUVs(0, mesh.uv?.data);

                        if (mesh.matrix == null) {
                            cmd.properties.SetFloatArray(CmdDraw.matId, CmdDraw.idMat3.fMat);
                        }
                        else {
                            var mat = mesh.matrix.Value;

                            this._drawLayer_matArray[0] = mat.kMScaleX;
                            this._drawLayer_matArray[1] = mat.kMSkewX;
                            this._drawLayer_matArray[2] = mat.kMTransX;
                            this._drawLayer_matArray[3] = mat.kMSkewY;
                            this._drawLayer_matArray[4] = mat.kMScaleY;
                            this._drawLayer_matArray[5] = mat.kMTransY;
                            this._drawLayer_matArray[6] = mat.kMPersp0;
                            this._drawLayer_matArray[7] = mat.kMPersp1;
                            this._drawLayer_matArray[8] = mat.kMPersp2;
                            cmd.properties.SetFloatArray(CmdDraw.matId, this._drawLayer_matArray);
                        }

                        cmdBuf.DrawMesh(cmd.meshObj, CmdDraw.idMat, cmd.material, 0, cmd.pass, cmd.properties.mpb);
                        if (cmd.layerId != null) {
                            cmdBuf.SetGlobalTexture(CmdDraw.texId, BuiltinRenderTextureType.None);
                        }

                        break;
                    case CmdScissor cmd:
                        this._setRenderTarget(cmdBuf, layer.rtID, ref toClear);

                        if (cmd.deviceScissor == null) {
                            cmdBuf.DisableScissorRect();
                        }
                        else {
                            cmdBuf.EnableScissorRect(uiRectHelper.toRect(cmd.deviceScissor.Value));
                        }

                        break;
                }
            }

            if (toClear) {
                this._setRenderTarget(cmdBuf, layer.rtID, ref toClear);
            }

            D.assert(!toClear);

            foreach (var subLayer in layer.layers) {
                cmdBuf.ReleaseTemporaryRT(subLayer.rtID);
            }
        }

        void _clearLayer(RenderLayer layer) {
            foreach (var cmdObj in layer.draws) {
                switch (cmdObj) {
                    case CmdDraw cmd:
                        if (cmd.meshObjCreated) {
                            this._meshPool.returnMesh(cmd.meshObj);
                            cmd.meshObj = null;
                            cmd.meshObjCreated = false;
                        }

                        break;
                }

                cmdObj.dispose();
            }

            layer.draws.Clear();

            foreach (var subLayer in layer.layers) {
                this._clearLayer(subLayer);
                subLayer.dispose();
            }

            layer.layers.Clear();
        }
    }
}