window.lineupCmafPlayers = new Map();
window.lineupCmafUis = new Map();
window.lineupCmafStarts = new Map();
window.lineupMediaPlayerOwners = new Map();

function dismissLineupShakaMenus(controls) {
    controls.dispatchEvent(new Event('submenuclose'));
    controls.hideSettingsMenus();
    const controlsContainer = controls.getControlsContainer();
    for (const menu of controlsContainer.querySelectorAll('.shaka-settings-menu, .shaka-overflow-menu, .shaka-sub-menu')) {
        menu.classList.add('shaka-hidden');
    }
    const overflowButton = controlsContainer.querySelector('.shaka-overflow-menu-button');
    overflowButton?.setAttribute('aria-expanded', 'false');
}

function registerLineupShakaUiElements(localizedText) {
    if (window.lineupShakaUiElementsRegistered) {
        return;
    }

    class LineupSubtitleSelection {
        constructor(parent, controls) {
            this.parent = parent;
            this.controls = controls;
            this.video = controls.getLocalVideo();
            this.onTracksChanged = () => this.updateTracks();
            this.onSubmenuClose = () => this.closeSubmenu();
            this.siblingVisibility = null;

            this.button = document.createElement('button');
            this.button.classList.add('shaka-overflow-button', 'shaka-lineup-subtitles-button');
            this.button.ariaLabel = localizedText.subtitles;
            this.button.innerHTML =
                '<label class="shaka-overflow-button-label shaka-overflow-menu-only shaka-overflow-button-label-inline">' +
                `<span>${localizedText.subtitles}</span><span class="shaka-current-selection-span"></span></label>`;
            this.currentSelection = this.button.querySelector('.shaka-current-selection-span');
            this.button.addEventListener('click', () => {
                this.openSubmenu();
            });
            this.parent.appendChild(this.button);

            this.menu = document.createElement('div');
            this.menu.classList.add('shaka-sub-menu', 'shaka-lineup-subtitles-menu', 'shaka-no-propagation', 'shaka-show-controls-on-mouse-over', 'shaka-hidden');
            this.menu.setAttribute('role', 'menu');
            this.parent.appendChild(this.menu);
            this.menuObserver = new MutationObserver(() => {
                if (this.menu.classList.contains('shaka-hidden')) {
                    this.restoreSiblings();
                }
            });
            this.menuObserver.observe(this.menu, { attributes: true, attributeFilter: ['class'] });
            this.video.addEventListener('lineuptexttrackschanged', this.onTracksChanged);
            this.controls.addEventListener('submenuclose', this.onSubmenuClose);
            this.updateTracks();
        }

        openSubmenu() {
            this.controls.dispatchEvent(new Event('submenuclose'));
            this.siblingVisibility = new Map();
            for (const sibling of this.parent.children) {
                if (sibling !== this.menu) {
                    this.siblingVisibility.set(sibling, sibling.classList.contains('shaka-hidden'));
                    sibling.classList.add('shaka-hidden');
                }
            }
            this.menu.classList.remove('shaka-hidden');
        }

        closeSubmenu() {
            this.menu.classList.add('shaka-hidden');
            this.restoreSiblings();
        }

        restoreSiblings() {
            for (const [sibling, wasHidden] of this.siblingVisibility || []) {
                sibling.classList.toggle('shaka-hidden', wasHidden);
            }
            this.siblingVisibility = null;
        }

        updateTracks() {
            const tracks = Array.from(this.video.textTracks || []);
            this.button.classList.toggle('shaka-hidden', tracks.length === 0);
            this.menu.replaceChildren();

            const back = document.createElement('button');
            back.classList.add('shaka-back-to-overflow-button');
            back.textContent = localizedText.subtitles;
            back.addEventListener('click', () => {
                this.closeSubmenu();
            });
            this.menu.appendChild(back);

            const selected = tracks.find(track => track.mode === 'showing');
            this.currentSelection.textContent = selected?.label || localizedText.off;
            this.menu.appendChild(this.createChoice(localizedText.off, !selected, () => {
                for (const track of tracks) {
                    track.mode = 'disabled';
                }
            }));
            for (const track of tracks) {
                this.menu.appendChild(this.createChoice(track.label || track.language || localizedText.subtitles, track === selected, () => {
                    for (const candidate of tracks) {
                        candidate.mode = candidate === track ? 'showing' : 'disabled';
                    }
                }));
            }
        }

        createChoice(label, selected, select) {
            const button = document.createElement('button');
            button.textContent = label;
            button.classList.toggle('shaka-chosen-item', selected);
            button.addEventListener('click', () => {
                select();
                this.updateTracks();
                dismissLineupShakaMenus(this.controls);
            });
            return button;
        }

        release() {
            this.menuObserver.disconnect();
            this.video.removeEventListener('lineuptexttrackschanged', this.onTracksChanged);
            this.controls.removeEventListener('submenuclose', this.onSubmenuClose);
            this.button.remove();
            this.menu.remove();
        }
    }

    shaka.ui.OverflowMenu.registerElement('lineup_captions', {
        create: (rootElement, controls) => new LineupSubtitleSelection(rootElement, controls)
    });
    window.lineupShakaUiElementsRegistered = true;
}
    
    async function initCmafPlayer(
        videoElementId,
        manifestUrl,
        protocolFallbackUrl,
        preferredAudio,
        preferredVideo,
        hasSourceVideoRendition,
        sourceVideoCodec,
        fallbackAudioCodec,
        subtitles,
        dotNetReference,
        sessionId,
        localizedText,
        playerOwnerId) {
        await stopMediaPlayer(videoElementId);

        const video = document.getElementById(videoElementId);
        if (!video) {
            return { success: false, error: localizedText.videoElementNotFound };
        }
        if (!window.shaka?.Player?.isBrowserSupported() || !window.shaka?.ui?.Overlay) {
            return { success: false, error: localizedText.shakaUnsupported };
        }
        const videoContainer = video.closest('[data-lineup-cmaf-container]');
        if (!videoContainer) {
            return { success: false, error: localizedText.playerContainerNotFound };
        }

        registerLineupShakaUiElements(localizedText);
        const player = new shaka.Player();
        const playerToken = Symbol('lineupCmafPlayer');
        window.lineupMediaPlayerOwners.set(videoElementId, playerOwnerId);
        video._lineupPlayerToken = playerToken;
        const isCurrentPlayer = () => video._lineupPlayerToken === playerToken;
        await player.attach(video);
        if (!isCurrentPlayer()) {
            await player.destroy();
            return { success: false, error: localizedText.initializationSuperseded };
        }
        player.configure({
            abr: { enabled: false },
            manifest: {
                dash: {
                    // FFmpeg packages independent CMAF tracks with absolute timestamps that Chromium can place differently.
                    sequenceMode: true
                }
            }
        });
        const ui = new shaka.ui.Overlay(player, videoContainer, video);
        ui.configure({
            controlPanelElements: ['play_pause', 'mute_volume', 'time_and_duration', 'spacer', 'overflow_menu', 'fullscreen'],
            overflowMenuButtons: ['lineup_captions', 'language', 'picture_in_picture'],
            trackLabelFormat: shaka.ui.Overlay.TrackLabelFormat.LABEL_OR_LANGUAGE,
            addSeekBar: true,
            showAudioChannelCountVariants: true,
            showAudioCodec: true
        });
        window.lineupCmafPlayers.set(videoElementId, player);
        window.lineupCmafUis.set(videoElementId, ui);
        window.lineupMediaPlayers ??= new Map();
        window.lineupMediaPlayers.set(videoElementId, video);

        let sourceFallbackApplied = false;
        let videoFallbackApplied = false;
        const codecMimeTypes = {
            aac: 'audio/mp4; codecs="mp4a.40.2"',
            ac3: 'audio/mp4; codecs="ac-3"',
            eac3: 'audio/mp4; codecs="ec-3"'
        };
        const supportsMediaSourceType = type => {
            const mediaSource = window.MediaSource || window.ManagedMediaSource;
            if (!type || !mediaSource?.isTypeSupported) {
                return null;
            }
            try {
                return mediaSource.isTypeSupported(type);
            } catch {
                return null;
            }
        };
        const sourceVideoSupported = sourceVideoCodec
            ? supportsMediaSourceType(`video/mp4; codecs="${sourceVideoCodec}"`)
            : null;
        const fallbackAudioSupported = supportsMediaSourceType(codecMimeTypes[fallbackAudioCodec] || null);
        const report = (kind, message, details = null) => {
            if (dotNetReference && isCurrentPlayer()) {
                void dotNetReference.invokeMethodAsync('OnCmafPlayerEvent', { kind, message, details, sessionId });
            }
        };
        let sessionUnavailable = false;
        const stopUnavailableSession = details => {
            if (sessionUnavailable || !isCurrentPlayer()) {
                return;
            }
            sessionUnavailable = true;
            report('error', localizedText.sessionUnavailable, details);
            void stopMediaPlayer(videoElementId);
        };
        const isSourceVideo = track =>
            hasSourceVideoRendition &&
            (!sourceVideoCodec ||
                (track.videoCodec || '').toLowerCase() === sourceVideoCodec.toLowerCase());
        const isFallbackVideo = track =>
            !isSourceVideo(track) &&
            /(^|[.,])avc[13]/i.test(track.videoCodec || '');
        const audioSourceKey = track => (track.label || '')
            .replace(/\s+\u00b7\s+(Source|Fallback.*)$/i, '')
            .toLowerCase();
        const choosePreferredVariant = () => {
            const variants = player.getVariantTracks().filter(track => track.audioCodec);
            const wantsSource = preferredAudio !== 'Fallback';
            const wantsSourceVideo = preferredVideo !== 'Fallback';
            const videoCandidates = variants.filter(track => wantsSourceVideo ? !isFallbackVideo(track) : isFallbackVideo(track));
            const candidates = videoCandidates.length > 0 ? videoCandidates : variants;
            const preferred = candidates.find(track => {
                const label = (track.label || '').toLowerCase();
                return wantsSource ? label.includes('source') : label.includes('fallback');
            }) || candidates[0];
            if (preferred && !preferred.active) {
                player.selectVariantTrack(preferred, true, 0);
            }
            const active = player.getVariantTracks().find(track => track.active);
            return active
                ? {
                    audio: { id: String(active.audioId ?? active.id), label: active.label, language: active.language, codec: active.audioCodec, channels: active.channelsCount },
                    video: { codec: active.videoCodec, width: active.width, height: active.height }
                }
                : { audio: null, video: null };
        };
        const tryAudioFallback = async error => {
            if (sourceFallbackApplied || preferredAudio === 'Fallback') {
                return false;
            }
            if (error?.category !== shaka.util.Error.Category.MEDIA) {
                return false;
            }
            const activeAudio = player.getVariantTracks().find(track => track.active);
            if (!activeAudio || (activeAudio.label || '').toLowerCase().includes('fallback')) {
                return false;
            }
            const sourceKey = audioSourceKey(activeAudio);
            const fallback = player.getVariantTracks().find(track => {
                const label = (track.label || '').toLowerCase();
                return label.includes('fallback') &&
                    track.videoId === activeAudio.videoId &&
                    (audioSourceKey(track) === sourceKey ||
                        (!sourceKey && track.language === activeAudio.language));
            });
            if (!fallback) {
                return false;
            }
            sourceFallbackApplied = true;
            player.selectVariantTrack(fallback, true, 0);
            report(
                'audio-fallback',
                localizedText.audioFallback,
                {
                    id: String(fallback.audioId ?? fallback.id),
                    label: fallback.label,
                    language: fallback.language,
                    codec: fallback.audioCodec,
                    channels: fallback.channelsCount
                });
            return true;
        };
        let presentationLoaded = false;
        const tryVideoFallback = async error => {
            if (!presentationLoaded || videoFallbackApplied || preferredVideo === 'Fallback' || error?.category !== shaka.util.Error.Category.MEDIA) {
                return false;
            }
            const activeVideo = player.getVariantTracks().find(track => track.active);
            if (!activeVideo || isFallbackVideo(activeVideo)) {
                return false;
            }
            const fallback = player.getVariantTracks().find(track =>
                isFallbackVideo(track) && track.audioId === activeVideo.audioId);
            if (!fallback) {
                videoFallbackApplied = true;
                report(
                    'video-fallback-required',
                    localizedText.videoFallbackRequired,
                    { codec: activeVideo.videoCodec, width: activeVideo.width, height: activeVideo.height });
                return true;
            }
            videoFallbackApplied = true;
            player.selectVariantTrack(fallback, true, 0);
            report(
                'video-fallback',
                localizedText.videoFallback,
                { codec: fallback.videoCodec, width: fallback.width, height: fallback.height });
            return true;
        };

        player.addEventListener('error', event => {
            void (async () => {
                const error = event.detail || null;
                const statusCode = Number(error?.data?.[1]);
                if (error?.category === shaka.util.Error.Category.NETWORK &&
                    error?.code === shaka.util.Error.Code.BAD_HTTP_STATUS &&
                    (statusCode === 404 || statusCode === 410)) {
                    stopUnavailableSession(error);
                } else if (!await tryVideoFallback(error) && !await tryAudioFallback(error)) {
                    report('error', localizedText.playbackFailed, error);
                }
            })();
        });
        player.addEventListener('audiotrackchanged', () => {
            dismissLineupShakaMenus(ui.getControls());
            const active = player.getVariantTracks().find(track => track.active);
            if (active) {
                report('audio-track-changed', localizedText.audioTrackChanged, {
                    id: String(active.audioId ?? active.id),
                    label: active.label,
                    language: active.language,
                    codec: active.audioCodec,
                    channels: active.channelsCount
                });
            }
        });

        let effectiveManifestUrl = manifestUrl;
        try {
            await player.load(manifestUrl);
        } catch (error) {
            if (!isCurrentPlayer()) {
                await ui.destroy();
                return { success: false, error: localizedText.initializationSuperseded };
            }
            if (!protocolFallbackUrl) {
                await ui.destroy();
                if (window.lineupCmafPlayers.get(videoElementId) === player) {
                    window.lineupCmafPlayers.delete(videoElementId);
                }
                if (window.lineupCmafUis.get(videoElementId) === ui) {
                    window.lineupCmafUis.delete(videoElementId);
                }
                return {
                    success: false,
                    error: error?.message || localizedText.presentationLoadFailed,
                    errorCode: error?.code || null,
                    sourceVideoSupported,
                    fallbackAudioSupported
                };
            }
            report('protocol-fallback', localizedText.protocolFallback);
            effectiveManifestUrl = protocolFallbackUrl;
            try {
                await player.load(protocolFallbackUrl);
            } catch (fallbackError) {
                await ui.destroy();
                if (window.lineupCmafPlayers.get(videoElementId) === player) {
                    window.lineupCmafPlayers.delete(videoElementId);
                }
                if (window.lineupCmafUis.get(videoElementId) === ui) {
                    window.lineupCmafUis.delete(videoElementId);
                }
                return {
                    success: false,
                    error: fallbackError?.message || localizedText.bothManifestsLoadFailed,
                    errorCode: fallbackError?.code || null,
                    sourceVideoSupported,
                    fallbackAudioSupported
                };
            }
        }

        presentationLoaded = true;
        if (!isCurrentPlayer()) {
            await ui.destroy();
            return { success: false, error: localizedText.initializationSuperseded };
        }
        const activeVariant = choosePreferredVariant();
        for (const subtitle of subtitles || []) {
            startLineupSubtitlePolling(
                video,
                subtitle.url,
                subtitle.label || localizedText.selectedSubtitles,
                subtitle.language || 'und',
                'hidden',
                false,
                () => {
                    const seekRange = player.seekRange();
                    return seekRange.end - video.currentTime;
                },
                () => stopUnavailableSession(null));
        }
        try {
            await video.play();
        } catch {
            // Browser autoplay policy may require user interaction; loaded playback remains usable.
        }
        return {
            success: true,
            manifestUrl: effectiveManifestUrl,
            audio: activeVariant.audio,
            video: activeVariant.video,
            sourceVideoSupported,
            fallbackAudioSupported
        };
    }

    function getCmafTestCapabilities() {
        const mediaSource = window.MediaSource || window.ManagedMediaSource;
        const supports = type => {
            if (!mediaSource?.isTypeSupported) {
                return null;
            }
            try {
                return mediaSource.isTypeSupported(type);
            } catch {
                return null;
            }
        };
        return {
            browserIdentity: `${navigator.userAgent}|${navigator.platform || ''}`,
            shakaSupported: window.shaka?.Player?.isBrowserSupported() === true,
            mediaSourceAvailable: !!mediaSource,
            nativeHls: document.createElement('video').canPlayType('application/vnd.apple.mpegurl') || '',
            h264: supports('video/mp4; codecs="avc1.64002a"'),
            hevc: supports('video/mp4; codecs="hvc1.1.6.L120"'),
            hevcMain10: supports('video/mp4; codecs="hvc1.2.4.L120"'),
            aac: supports('audio/mp4; codecs="mp4a.40.2"'),
            ac3: supports('audio/mp4; codecs="ac-3"'),
            eac3: supports('audio/mp4; codecs="ec-3"'),
            ac4: supports('audio/mp4; codecs="ac-4"')
        };
    }

    function getCmafBrowserIdentity() {
        return `${navigator.userAgent}|${navigator.platform || ''}`;
    }

    async function initCmafTestPlayer(videoElementId, manifestUrl, expectedVideoCodec, expectedAudioCodec, subtitleUrl, dotNetReference, sessionId, localizedText, playerOwnerId) {
        await stopMediaPlayer(videoElementId);
        const video = document.getElementById(videoElementId);
        if (!video) {
            return { success: false, playbackStarted: false, error: localizedText.videoElementNotFound };
        }
        if (!window.shaka?.Player?.isBrowserSupported()) {
            return { success: false, playbackStarted: false, error: localizedText.shakaUnsupported };
        }

        const player = new shaka.Player();
        window.lineupMediaPlayerOwners.set(videoElementId, playerOwnerId);
        await player.attach(video);
        player.configure({
            abr: { enabled: false },
            manifest: {
                dash: {
                    // Keep compatibility-test playback identical to the Watch player.
                    sequenceMode: true
                }
            }
        });
        window.lineupCmafPlayers.set(videoElementId, player);
        window.lineupMediaPlayers ??= new Map();
        window.lineupMediaPlayers.set(videoElementId, video);
        const mediaSource = window.MediaSource || window.ManagedMediaSource;
        const supports = type => {
            if (!type || !mediaSource?.isTypeSupported) {
                return null;
            }
            try {
                return mediaSource.isTypeSupported(type);
            } catch {
                return null;
            }
        };
        const videoSupported = supports(expectedVideoCodec ? `video/mp4; codecs="${expectedVideoCodec}"` : null);
        const audioSupported = supports(expectedAudioCodec ? `audio/mp4; codecs="${expectedAudioCodec}"` : null);
        let resolvePlaybackError;
        const playbackError = new Promise(resolve => {
            resolvePlaybackError = resolve;
        });
        player.addEventListener('error', event => {
            const error = event.detail || null;
            const runtimeError = {
                sessionId,
                message: error?.message || localizedText.compatibilityPlaybackFailed,
                errorCode: error?.code || null,
                details: error ? {
                    severity: error.severity ?? null,
                    category: error.category ?? null,
                    code: error.code ?? null,
                    data: error.data ?? null
                } : null
            };
            resolvePlaybackError(runtimeError);
            if (dotNetReference) {
                void dotNetReference.invokeMethodAsync('OnCmafTestPlayerError', runtimeError);
            }
        });

        let playbackStarted = false;
        let subtitleCueVisible = false;
        const playing = new Promise(resolve => {
            video.addEventListener('playing', () => {
                playbackStarted = true;
                resolve(true);
            }, { once: true });
        });
        try {
            await player.load(manifestUrl);
            if (subtitleUrl) {
                const track = document.createElement('track');
                track.kind = 'subtitles';
                track.label = localizedText.compatibilityTrackLabel;
                track.srclang = 'en';
                track.src = subtitleUrl;
                track.default = true;
                video.appendChild(track);
                await new Promise(resolve => {
                    const timeout = setTimeout(resolve, 3000);
                    track.addEventListener('load', () => {
                        clearTimeout(timeout);
                        track.track.mode = 'showing';
                        subtitleCueVisible = (track.track.cues?.length ?? 0) > 0;
                        resolve();
                    }, { once: true });
                });
            }
            const variants = player.getVariantTracks();
            const selected = variants[0] || null;
            if (selected && !selected.active) {
                player.selectVariantTrack(selected, true, 0);
            }
            try {
                await video.play();
            } catch {
                // The result distinguishes autoplay policy from manifest and decoder support.
            }
            const startupError = await Promise.race([
                playing.then(() => null),
                playbackError,
                new Promise(resolve => setTimeout(() => resolve(null), 5000))
            ]);
            if (startupError) {
                return {
                    success: false,
                    playbackStarted,
                    videoSupported,
                    audioSupported,
                    error: startupError.message,
                    errorCode: startupError.errorCode,
                    errorDetails: startupError.details
                };
            }
            if (playbackStarted) {
                const stabilityError = await Promise.race([
                    playbackError,
                    new Promise(resolve => setTimeout(() => resolve(null), 2000))
                ]);
                if (stabilityError) {
                    return {
                        success: false,
                        playbackStarted: false,
                        videoSupported,
                        audioSupported,
                        error: stabilityError.message,
                        errorCode: stabilityError.errorCode,
                        errorDetails: stabilityError.details
                    };
                }
            }
            const active = player.getVariantTracks().find(track => track.active) || selected;
            return {
                success: true,
                playbackStarted,
                manifestUrl,
                videoSupported,
                audioSupported,
                subtitleCueVisible,
                audio: active ? { label: active.label, codec: active.audioCodec, channels: active.channelsCount } : null,
                video: active ? { codec: active.videoCodec, width: active.width, height: active.height } : null
            };
        } catch (error) {
            await player.destroy();
            window.lineupCmafPlayers.delete(videoElementId);
            return {
                success: false,
                playbackStarted: false,
                videoSupported,
                audioSupported,
                error: error?.message || localizedText.compatibilityPresentationLoadFailed,
                errorCode: error?.code || null,
                errorDetails: error ? {
                    severity: error.severity ?? null,
                    category: error.category ?? null,
                    code: error.code ?? null,
                    data: error.data ?? null
                } : null
            };
        }
    }

    async function startCmafSession(startUrl, requestBody, startupId) {
        const controller = new AbortController();
        if (startupId) {
            window.lineupCmafStarts.set(startupId, controller);
        }
        const options = { method: 'POST', cache: 'no-store', signal: controller.signal };
        if (requestBody !== undefined && requestBody !== null) {
            options.headers = { 'Content-Type': 'application/json' };
            options.body = JSON.stringify(requestBody);
        }
        try {
            const response = await fetch(startUrl, options);
            const body = await response.json().catch(() => null);
            if (!response.ok) {
                const message = body?.error || `Unable to start CMAF playback (${response.status}).`;
                const codePrefix = body?.code && !message.startsWith(`${body.code} `)
                    ? `${body.code} `
                    : '';
                throw new Error(`${codePrefix}${message}`);
            }
            return body;
        } finally {
            if (startupId && window.lineupCmafStarts.get(startupId) === controller) {
                window.lineupCmafStarts.delete(startupId);
            }
        }
    }

    function cancelCmafStart(startupId) {
        const controller = startupId ? window.lineupCmafStarts.get(startupId) : null;
        if (!controller) {
            return;
        }
        window.lineupCmafStarts.delete(startupId);
        controller.abort();
    }

    async function stopCmafSession(sessionId, stopUrl) {
        if (!sessionId) {
            return;
        }
        if (!stopUrl) {
            return;
        }
        await fetch(stopUrl, {
            method: 'POST',
            cache: 'no-store',
            keepalive: true
        });
    }

    function stopLineupSubtitlePolling(video) {
        if (!video) {
            return;
        }
        for (const poller of video._lineupSubtitlePollers || []) {
            poller.abortController.abort();
            clearInterval(poller.timer);
            for (const cue of Array.from(poller.track.cues || [])) {
                poller.track.removeCue(cue);
            }
            poller.track.mode = 'disabled';
            poller.trackElement.remove();
        }
        video._lineupSubtitlePollers = [];
        video.dispatchEvent(new Event('lineuptexttrackschanged'));
    }

    async function stopMediaPlayer(videoElementId = 'videoPlayer', playerOwnerId = null) {
        if (playerOwnerId && window.lineupMediaPlayerOwners?.get(videoElementId) !== playerOwnerId) {
            return;
        }
        const currentVideo = document.getElementById(videoElementId);
        const registeredVideo = window.lineupMediaPlayers?.get(videoElementId);
        const videos = [...new Set([currentVideo, registeredVideo].filter(Boolean))];
        for (const video of videos) {
            video._lineupPlayerToken = null;
            stopLineupSubtitlePolling(video);
            video.onerror = null;
            video.onloadedmetadata = null;
            for (const track of video.textTracks) {
                for (const cue of Array.from(track.cues || [])) {
                    track.removeCue(cue);
                }
                track.mode = 'disabled';
            }
            if (video._lineupInitializationResolve) {
                video._lineupInitializationResolve(null);
                video._lineupInitializationResolve = null;
            }
            video.pause();
        }

        const cmafUi = window.lineupCmafUis?.get(videoElementId);
        const cmafPlayer = window.lineupCmafPlayers?.get(videoElementId);
        if (cmafUi) {
            await Promise.allSettled([cmafUi.destroy()]);
            if (window.lineupCmafUis.get(videoElementId) === cmafUi) {
                window.lineupCmafUis.delete(videoElementId);
            }
            if (window.lineupCmafPlayers.get(videoElementId) === cmafPlayer) {
                window.lineupCmafPlayers.delete(videoElementId);
            }
        } else if (cmafPlayer) {
            await Promise.allSettled([cmafPlayer.destroy()]);
            if (window.lineupCmafPlayers.get(videoElementId) === cmafPlayer) {
                window.lineupCmafPlayers.delete(videoElementId);
            }
        }

        for (const video of videos) {
            video.removeAttribute('src');
            video.load();
        }
        window.lineupMediaPlayers?.delete(videoElementId);
        if (!playerOwnerId || window.lineupMediaPlayerOwners?.get(videoElementId) === playerOwnerId) {
            window.lineupMediaPlayerOwners?.delete(videoElementId);
        }
    }

    function startLineupSubtitlePolling(
        video,
        subtitleUrl,
        label = '',
        language = 'und',
        initialMode = 'showing',
        replaceExisting = true,
        getPlaybackDelaySeconds = null,
        onSessionUnavailable = null) {
        if (replaceExisting) {
            stopLineupSubtitlePolling(video);
        }
        const trackElement = document.createElement('track');
        trackElement.kind = 'subtitles';
        trackElement.label = label;
        trackElement.srclang = language;
        trackElement.src = 'data:text/vtt;charset=utf-8,WEBVTT%0A%0A';
        trackElement.addEventListener(
            'load',
            () => video.dispatchEvent(new Event('lineuptexttrackschanged')),
            { once: true });
        video.appendChild(trackElement);
        const track = trackElement.track;
        track.mode = initialMode;
        const cuesByKey = new Map();
        const decoder = new TextDecoder('utf-8');
        const abortController = new AbortController();
        const presentationLeadSeconds = 0.05;
        let offset = 0;
        let pendingText = '';
        let refreshPending = false;
        const parseTime = value => {
            const parts = value.trim().replace(',', '.').split(':').map(Number);
            return parts.length === 3 ? parts[0] * 3600 + parts[1] * 60 + parts[2] : parts[0] * 60 + parts[1];
        };
        const refresh = async () => {
            if (refreshPending || abortController.signal.aborted) {
                return;
            }
            if (!video.isConnected) {
                stopLineupSubtitlePolling(video);
                return;
            }
            refreshPending = true;
            try {
                const separator = subtitleUrl.includes('?') ? '&' : '?';
                const response = await fetch(
                    `${subtitleUrl}${separator}offset=${offset}`,
                    { cache: 'no-store', signal: abortController.signal });
                if (response.status === 404 || response.status === 410) {
                    if (typeof onSessionUnavailable === 'function') {
                        onSessionUnavailable();
                    } else {
                        stopLineupSubtitlePolling(video);
                    }
                    return;
                }
                if (!response.ok) {
                    return;
                }
                const nextOffset = Number(response.headers.get('X-Lineup-Subtitle-Offset'));
                if (Number.isFinite(nextOffset)) {
                    offset = nextOffset;
                }
                pendingText += decoder.decode(await response.arrayBuffer(), { stream: true }).replace(/\r/g, '');
                const blocks = pendingText.split(/\n\n+/);
                pendingText = blocks.pop() || '';
                const parsedCues = [];
                for (const block of blocks) {
                    const lines = block.split('\n').filter(Boolean);
                    const timingIndex = lines.findIndex(line => line.includes('-->'));
                    if (timingIndex < 0) {
                        continue;
                    }
                    const [startText, endTextWithSettings] = lines[timingIndex].split('-->');
                    const endText = endTextWithSettings.trim().split(/\s+/)[0];
                    const text = lines.slice(timingIndex + 1).join('\n').replace(/\\h/g, '\u00a0');
                    const key = `${startText}|${endText}|${text}`;
                    if (!cuesByKey.has(key)) {
                        parsedCues.push({
                            key,
                            startTime: parseTime(startText),
                            endTime: parseTime(endText),
                            text
                        });
                    }
                }
                const newestCue = parsedCues.at(-1);
                const playbackDelaySeconds = typeof getPlaybackDelaySeconds === 'function'
                    ? Number(getPlaybackDelaySeconds())
                    : 0;
                const effectivePlaybackDelaySeconds = Number.isFinite(playbackDelaySeconds)
                    ? Math.max(0, playbackDelaySeconds)
                    : 0;
                const presentationOffset = newestCue
                    ? Math.max(0, video.currentTime + effectivePlaybackDelaySeconds + presentationLeadSeconds - newestCue.startTime)
                    : 0;
                for (const parsedCue of parsedCues) {
                    const cue = new VTTCue(
                        parsedCue.startTime + presentationOffset,
                        parsedCue.endTime + presentationOffset,
                        parsedCue.text);
                    if (!cuesByKey.has(parsedCue.key)) {
                        const key = parsedCue.key;
                        cuesByKey.set(key, cue);
                        track.addCue(cue);
                    }
                }
                const retentionBoundary = Math.max(0, video.currentTime - 30);
                for (const [key, cue] of cuesByKey) {
                    if (cue.endTime < retentionBoundary) {
                        track.removeCue(cue);
                        cuesByKey.delete(key);
                    }
                }
                while (cuesByKey.size > 1000) {
                    const oldest = cuesByKey.entries().next().value;
                    track.removeCue(oldest[1]);
                    cuesByKey.delete(oldest[0]);
                }
            } catch (error) {
                if (error.name === 'AbortError') {
                    return;
                }
                // The sidecar may not exist until FFmpeg emits its first cue.
            } finally {
                refreshPending = false;
            }
        };
        const timer = setInterval(refresh, 100);
        video._lineupSubtitlePollers ??= [];
        video._lineupSubtitlePollers.push({ abortController, timer, track, trackElement });
        video.dispatchEvent(new Event('lineuptexttrackschanged'));
        refresh();
    }
