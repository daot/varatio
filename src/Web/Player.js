(() => {
  console.log("VARatio server-side stream script loaded");

  let currentItemId = null;
  let observer = null;
  let domCheckTimeout = null;

  function getVideoElement() {
    return (
      document.querySelector(".htmlVideoplayer") ||
      document.querySelector("video")
    );
  }

  function resolveItemIdSync() {
    try {
      // Prefer URL query (Jellyfin hash/router still exposes ?id= in many views)
      const url = new URL(window.location.href);
      const fromUrl = url.searchParams.get("id");
      if (fromUrl) {
        console.log("VARatio: resolveItemIdSync from URL", fromUrl);
        return fromUrl;
      }

      // Fallback to Jellyfin ApiClient state if available
      if (
        window.ApiClient &&
        window.ApiClient.lastPlaybackProgressOptions &&
        window.ApiClient.lastPlaybackProgressOptions.ItemId
      ) {
        const fromProgress = window.ApiClient.lastPlaybackProgressOptions.ItemId;
        console.log("VARatio: resolveItemIdSync from lastPlaybackProgressOptions", fromProgress);
        return fromProgress;
      }
    } catch (e) {
      console.error("VARatio: resolveItemIdSync failed", e);
    }
    return null;
  }

  async function resolveItemId() {
    // Try cheap synchronous paths first
    const immediate = resolveItemIdSync();
    if (immediate) {
      return immediate;
    }

    // As a fallback, query active sessions via ApiClient
    try {
      if (!window.ApiClient || typeof window.ApiClient.getSessions !== "function") {
        console.log("VARatio: ApiClient.getSessions not available");
        return null;
      }

      const currentUserId =
        typeof window.ApiClient.getCurrentUserId === "function"
          ? window.ApiClient.getCurrentUserId()
          : null;

      const sessions = await window.ApiClient.getSessions({
        activeWithinSeconds: 300,
      });

      if (Array.isArray(sessions)) {
        for (let i = 0; i < sessions.length; i++) {
          const s = sessions[i];
          if (!s.NowPlayingItem || !s.NowPlayingItem.Id) {
            continue;
          }
          if (currentUserId && s.UserId && s.UserId !== currentUserId) {
            continue;
          }
          console.log("VARatio: resolveItemId from sessions", s.NowPlayingItem.Id);
          return s.NowPlayingItem.Id;
        }
      }
    } catch (e) {
      console.error("VARatio: resolveItemId from sessions failed", e);
    }

    return null;
  }

  function resolveItemIdForPlay(video) {
    // 1) Try to parse from the video's currentSrc URL
    try {
      if (video && video.currentSrc) {
        const u = new URL(video.currentSrc, window.location.origin);
        const p = u.searchParams;
        const fromParams =
          p.get("itemId") ||
          p.get("ItemId") ||
          p.get("id") ||
          p.get("MediaSourceId");
        if (fromParams) {
          console.log("VARatio: resolveItemId from currentSrc params", fromParams);
          return fromParams;
        }

        const segments = u.pathname.split("/");
        const guidRegex =
          /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
        for (let i = 0; i < segments.length; i++) {
          const s = segments[i];
          if (guidRegex.test(s)) {
            console.log("VARatio: resolveItemId from currentSrc path", s);
            return s;
          }
        }
      }
    } catch (e) {
      console.error("VARatio: resolveItemIdForPlay from currentSrc failed", e);
    }

    // 2) Fallback to our synchronous helpers (URL + lastPlaybackProgressOptions)
    const sync = resolveItemIdSync();
    if (sync) {
      return sync;
    }

    return null;
  }

  async function hasVarData(itemId) {
    try {
      const headers = {};
      if (
        window.ApiClient &&
        typeof window.ApiClient.accessToken === "function"
      ) {
        headers["Authorization"] =
          'MediaBrowser Token="' + window.ApiClient.accessToken() + '"';
      }

      const response = await fetch("/VARatio/Data?itemId=" + itemId, {
        method: "GET",
        headers,
      });

      return response.ok;
    } catch (e) {
      console.error("VARatio: Failed to query VAR data", e);
      return false;
    }
  }

  async function switchToVarStream(itemId) {
    if (!itemId) {
      return;
    }
    if (currentItemId === itemId) {
      return;
    }

    const video = getVideoElement();
    if (!video) {
      return;
    }

    const ok = await hasVarData(itemId);
    if (!ok) {
      console.log("VARatio: No .var data for item", itemId);
      return;
    }

    const streamUrl = "/VARatio/Stream?itemId=" + itemId;

    try {
      console.log("VARatio: Switching video source to", streamUrl);
      currentItemId = itemId;

      video.pause();
      video.src = streamUrl;
      video.load();
      await video.play();
    } catch (e) {
      console.error("VARatio: Failed to switch to VAR stream", e);
    }
  }

  function attachToVideo(video) {
    if (!video || video.dataset.varatioAttached === "true") {
      return;
    }
    video.dataset.varatioAttached = "true";
    console.log("VARatio: Attached to video element");

    let switchedOnce = false;

    video.addEventListener("play", () => {
      if (switchedOnce) {
        return;
      }
      // Use GUID from video URL (session/MediaSource id); server resolves to library item for .var lookup
      const itemId = resolveItemIdForPlay(video);
      if (!itemId) {
        console.log("VARatio: play event but no itemId");
        return;
      }
      switchedOnce = true;
      switchToVarStream(itemId);
    });
  }

  function triggerDomCheck() {
    if (domCheckTimeout) {
      return;
    }
    domCheckTimeout = setTimeout(() => {
      domCheckTimeout = null;
      const video = getVideoElement();
      if (video) {
        attachToVideo(video);
      }
    }, 300);
  }

  function initVideoObserver() {
    if (observer) {
      return;
    }
    observer = new MutationObserver((mutations) => {
      for (let i = 0; i < mutations.length; i++) {
        if (mutations[i].addedNodes && mutations[i].addedNodes.length > 0) {
          triggerDomCheck();
          break;
        }
      }
    });

    if (document.body) {
      observer.observe(document.body, { childList: true, subtree: true });
      triggerDomCheck();
    } else {
      window.addEventListener("DOMContentLoaded", () => {
        observer.observe(document.body, { childList: true, subtree: true });
        triggerDomCheck();
      });
    }
  }

  initVideoObserver();

  document.addEventListener("playbackstart", (e) => {
    try {
      if (!e || !e.detail || !e.detail.item || !e.detail.item.Id) {
        return;
      }
      const itemId = e.detail.item.Id;
      console.log("VARatio: playbackstart for item", itemId);
      switchToVarStream(itemId);
    } catch (err) {
      console.error("VARatio: playbackstart handler error", err);
    }
  });
})();
