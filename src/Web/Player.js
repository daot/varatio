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

  function resolveItemId() {
    try {
      // Prefer URL query
      const url = new URL(window.location.href);
      const fromUrl = url.searchParams.get("id");
      if (fromUrl) {
        return fromUrl;
      }

      // Fallback to Jellyfin ApiClient state
      if (
        window.ApiClient &&
        window.ApiClient.lastPlaybackProgressOptions &&
        window.ApiClient.lastPlaybackProgressOptions.ItemId
      ) {
        return window.ApiClient.lastPlaybackProgressOptions.ItemId;
      }
    } catch (e) {
      console.error("VARatio: resolveItemId failed", e);
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
      const itemId = resolveItemId();
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
