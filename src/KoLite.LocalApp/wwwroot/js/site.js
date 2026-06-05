(function () {
  var sliceTooltip;
  var successChartPalette = [
    "#0969da",
    "#1a7f37",
    "#8250df",
    "#bf3989",
    "#9a6700",
    "#cf222e",
    "#1b7c83",
    "#6f42c1",
    "#d4a72c",
    "#57606a"
  ];
  var jobChartPalette = ["#1a7f37", "#d4a72c", "#cf222e", "#0969da", "#8250df"];
  var successRateChartEntries = [];

  function tooltipRow(label, value) {
    var row = document.createElement("div");
    row.className = "tooltip-row";

    var labelNode = document.createElement("span");
    labelNode.className = "tooltip-label";
    labelNode.textContent = label;
    row.appendChild(labelNode);

    var valueNode = document.createElement("span");
    valueNode.className = "tooltip-value";
    valueNode.textContent = value || "-";
    row.appendChild(valueNode);

    return row;
  }

  function ensureSliceTooltip() {
    if (sliceTooltip) return sliceTooltip;

    sliceTooltip = document.createElement("div");
    sliceTooltip.className = "slice-history-tooltip";
    sliceTooltip.setAttribute("role", "tooltip");
    document.body.appendChild(sliceTooltip);
    return sliceTooltip;
  }

  function positionTooltip(target, tooltip) {
    var rect = target.getBoundingClientRect();
    var margin = 8;
    var left = Math.max(margin, Math.min(rect.left, window.innerWidth - tooltip.offsetWidth - margin));
    var top = rect.top - tooltip.offsetHeight - margin;
    if (top < margin) top = rect.bottom + margin;
    tooltip.style.left = left + "px";
    tooltip.style.top = top + "px";
  }

  function sliceTooltipRows(target) {
    var lineNodes = target.querySelectorAll("[data-slice-tooltip-line='true']");
    if (lineNodes.length) {
      return Array.prototype.map.call(lineNodes, function (lineNode) {
        return {
          label: lineNode.getAttribute("data-tooltip-label"),
          value: lineNode.getAttribute("data-tooltip-value")
        };
      });
    }

    return [
      { label: "Start", value: target.getAttribute("data-slice-start") },
      { label: "End", value: target.getAttribute("data-slice-end") },
      { label: "Status", value: target.getAttribute("data-slice-status") },
      { label: "Attempt", value: target.getAttribute("data-slice-attempt") }
    ];
  }

  function showSliceTooltip(target) {
    var tooltip = ensureSliceTooltip();
    tooltip.classList.remove("visible");
    tooltip.replaceChildren();

    var title = document.createElement("div");
    title.className = "tooltip-title";
    title.textContent = target.getAttribute("data-slice-tooltip-title") || "Slice";
    tooltip.appendChild(title);

    sliceTooltipRows(target).forEach(function (row) {
      tooltip.appendChild(tooltipRow(row.label, row.value));
    });

    tooltip.style.left = "0";
    tooltip.style.top = "0";
    positionTooltip(target, tooltip);
    tooltip.classList.add("visible");
  }

  function hideSliceTooltip() {
    if (sliceTooltip) sliceTooltip.classList.remove("visible");
  }

  function formatUtcTick(value, rangeMs) {
    var date = new Date(Number(value));
    if (Number.isNaN(date.getTime())) return "";

    var month = String(date.getUTCMonth() + 1).padStart(2, "0");
    var day = String(date.getUTCDate()).padStart(2, "0");
    var hour = String(date.getUTCHours()).padStart(2, "0");
    var minute = String(date.getUTCMinutes()).padStart(2, "0");
    if (rangeMs <= 60 * 60 * 1000) return hour + ":" + minute;
    if (rangeMs <= 24 * 60 * 60 * 1000) return month + "/" + day + " " + hour + ":" + minute;
    return month + "/" + day;
  }

  function formatDurationTick(value) {
    var ms = Number(value);
    if (!Number.isFinite(ms)) return "";
    if (ms < 1000) return Math.round(ms) + " ms";
    var seconds = ms / 1000;
    if (seconds < 60) return seconds.toFixed(seconds < 10 ? 1 : 0) + " s";
    var minutes = seconds / 60;
    if (minutes < 60) return minutes.toFixed(minutes < 10 ? 1 : 0) + " m";
    var hours = minutes / 60;
    return hours.toFixed(hours < 10 ? 1 : 0) + " h";
  }

  function toggleSuccessRateSeries(event, legendItem, legend) {
    var chart = legend.chart;
    var datasetIndex = legendItem.datasetIndex;
    var allVisible = chart.data.datasets.every(function (_, index) {
      return chart.isDatasetVisible(index);
    });

    if (allVisible) {
      chart.data.datasets.forEach(function (_, index) {
        chart.setDatasetVisibility(index, index === datasetIndex);
      });
      chart.update();
      return;
    }

    chart.setDatasetVisibility(datasetIndex, !chart.isDatasetVisible(datasetIndex));
    var anyVisible = chart.data.datasets.some(function (_, index) {
      return chart.isDatasetVisible(index);
    });
    if (!anyVisible) {
      chart.data.datasets.forEach(function (_, index) {
        chart.setDatasetVisibility(index, true);
      });
    }

    chart.update();
  }

  function buildSuccessRateChart(canvas, payload) {
    if (!window.Chart || !canvas || !payload || !payload.series || payload.series.length === 0) return null;

    var rangeStart = Date.parse(payload.rangeStartUtc);
    var rangeEnd = Date.parse(payload.rangeEndUtc);
    var rangeMs = Math.max(0, rangeEnd - rangeStart);
    var datasets = payload.series.map(function (series, index) {
      var color = successChartPalette[index % successChartPalette.length];
      return {
        label: series.name,
        jobId: series.name,
        data: series.points.map(function (point) {
          return {
            x: point.x,
            y: point.y,
            numerator: point.numerator,
            denominator: point.denominator,
            bucket: point.bucket,
            label: point.label,
            percentText: point.percentText
          };
        }),
        borderColor: color,
        backgroundColor: color,
        borderWidth: 2.25,
        pointBorderColor: "#fff",
        pointBorderWidth: 1.25,
        pointHitRadius: 10,
        pointHoverRadius: 10,
        pointRadius: function (context) {
          return context.raw && context.raw.y !== null ? 5.5 : 0;
        },
        tension: 0.22,
        spanGaps: false
      };
    });

    return new Chart(canvas, {
      type: "line",
      data: { datasets: datasets },
      options: {
        animation: false,
        maintainAspectRatio: false,
        normalized: true,
        parsing: false,
        interaction: {
          intersect: false,
          mode: "nearest"
        },
        plugins: {
          legend: {
            display: true,
            position: "bottom",
            labels: {
              boxWidth: 28,
              color: "#24292f",
              font: { size: 12 },
              usePointStyle: true
            },
            onClick: toggleSuccessRateSeries
          },
          tooltip: {
            callbacks: {
              title: function (items) {
                var raw = items.length ? items[0].raw : null;
                return raw ? raw.bucket : "";
              },
              label: function (context) {
                var raw = context.raw || {};
                var value = raw.y === null || typeof raw.y === "undefined" ? "n/a" : raw.percentText;
                var count = typeof raw.numerator === "number" && typeof raw.denominator === "number"
                  ? " (" + raw.numerator + "/" + raw.denominator + ")"
                  : "";
                return context.dataset.label + ": " + value + count;
              }
            }
          }
        },
        scales: {
          x: {
            type: "linear",
            min: rangeStart,
            max: rangeEnd,
            grid: { color: "rgba(208, 215, 222, 0.55)" },
            ticks: {
              color: "#57606a",
              maxRotation: 0,
              callback: function (value) { return formatUtcTick(value, rangeMs); }
            }
          },
          y: {
            min: 0,
            max: 100,
            grid: { color: "rgba(208, 215, 222, 0.75)" },
            ticks: {
              color: "#57606a",
              stepSize: 25,
              callback: function (value) { return value + "%"; }
            },
            title: {
              display: true,
              text: "Success",
              color: "#57606a"
            }
          }
        }
      }
    });
  }

  function initSuccessRateCharts() {
    document.querySelectorAll("[data-chartjs-success]").forEach(function (container) {
      var chartId = container.getAttribute("data-chartjs-success");
      var canvas = document.getElementById(chartId);
      var payloadNode = document.getElementById(chartId + "-data");
      if (!canvas || !payloadNode) return;

      try {
        var payload = JSON.parse(payloadNode.textContent || "{}");
        var chart = buildSuccessRateChart(canvas, payload);
        if (chart) {
          successRateChartEntries.push({
            chart: chart,
            datasets: chart.data.datasets.slice()
          });
        }
      } catch (error) {
        container.classList.add("chart-error");
        var message = document.createElement("p");
        message.className = "empty";
        message.textContent = "Chart data could not be rendered.";
        container.prepend(message);
        throw error;
      }
    });
  }

  function buildJobDetailChart(canvas, payload) {
    if (!window.Chart || !canvas || !payload || !payload.series || payload.series.length === 0) return null;

    var rangeStart = Date.parse(payload.rangeStartUtc);
    var rangeEnd = Date.parse(payload.rangeEndUtc);
    var rangeMs = Math.max(0, rangeEnd - rangeStart);
    var isDurationChart = payload.kind === "duration";
    var datasets = payload.series.map(function (series, index) {
      var color = series.color || jobChartPalette[index % jobChartPalette.length];
      return {
        label: series.name,
        data: (series.points || []).map(function (point) {
          return {
            x: point.x,
            y: point.y,
            count: point.count,
            missingCount: point.missingCount,
            bucket: point.bucket,
            label: point.label,
            durationText: point.durationText
          };
        }),
        borderColor: color,
        backgroundColor: color,
        borderWidth: 2,
        pointBorderColor: "#fff",
        pointBorderWidth: 1.25,
        pointHitRadius: 10,
        pointHoverRadius: 10,
        pointRadius: function (context) {
          var raw = context.raw || {};
          return raw.y !== null && typeof raw.y !== "undefined" && raw.y !== 0 ? 5.5 : 0;
        },
        tension: isDurationChart ? 0.18 : 0,
        spanGaps: isDurationChart
      };
    });

    return new Chart(canvas, {
      type: "line",
      data: { datasets: datasets },
      options: {
        animation: false,
        maintainAspectRatio: false,
        normalized: true,
        parsing: false,
        interaction: {
          intersect: false,
          mode: "nearest"
        },
        plugins: {
          legend: {
            display: true,
            position: "bottom",
            labels: {
              boxWidth: 28,
              color: "#24292f",
              font: { size: 12 },
              usePointStyle: true
            },
            onClick: toggleSuccessRateSeries
          },
          tooltip: {
            callbacks: {
              title: function (items) {
                var raw = items.length ? items[0].raw : null;
                return raw ? raw.bucket : "";
              },
              label: function (context) {
                var raw = context.raw || {};
                if (isDurationChart) {
                  var included = typeof raw.count === "number" ? raw.count : 0;
                  var missing = typeof raw.missingCount === "number" ? raw.missingCount : 0;
                  var coverage = " (" + included + " included";
                  if (missing > 0) coverage += ", " + missing + " missing duration";
                  coverage += ")";
                  return context.dataset.label + ": " + (raw.durationText || "n/a") + coverage;
                }

                return context.dataset.label + ": " + (raw.count || 0) + " execution(s)";
              }
            }
          }
        },
        scales: {
          x: {
            type: "linear",
            min: rangeStart,
            max: rangeEnd,
            grid: { color: "rgba(208, 215, 222, 0.55)" },
            ticks: {
              color: "#57606a",
              maxRotation: 0,
              callback: function (value) { return formatUtcTick(value, rangeMs); }
            }
          },
          y: {
            beginAtZero: true,
            grid: { color: "rgba(208, 215, 222, 0.75)" },
            ticks: {
              color: "#57606a",
              precision: isDurationChart ? undefined : 0,
              callback: function (value) {
                return isDurationChart ? formatDurationTick(value) : value;
              }
            },
            title: {
              display: true,
              text: payload.yAxisTitle || (isDurationChart ? "Duration" : "Executions"),
              color: "#57606a"
            }
          }
        }
      }
    });
  }

  function initJobDetailCharts() {
    document.querySelectorAll("[data-chartjs-job]").forEach(function (container) {
      var chartId = container.getAttribute("data-chartjs-job");
      var canvas = document.getElementById(chartId);
      var payloadNode = document.getElementById(chartId + "-data");
      if (!canvas || !payloadNode) return;

      try {
        var payload = JSON.parse(payloadNode.textContent || "{}");
        buildJobDetailChart(canvas, payload);
      } catch (error) {
        container.classList.add("chart-error");
        var message = document.createElement("p");
        message.className = "empty";
        message.textContent = "Chart data could not be rendered.";
        container.prepend(message);
        throw error;
      }
    });
  }

  function tabId(tab) {
    var href = tab.getAttribute("href") || "";
    return href.charAt(0) === "#" ? href.slice(1) : "";
  }

  function activateTab(root, selectedTab, focusTab) {
    var tabs = Array.prototype.slice.call(root.querySelectorAll("[role='tab'][href^='#']"));
    tabs.forEach(function (tab) {
      var selected = tab === selectedTab;
      var id = tabId(tab);
      var panel = id ? document.getElementById(id) : null;
      tab.classList.toggle("active", selected);
      tab.setAttribute("aria-selected", selected ? "true" : "false");
      tab.tabIndex = selected ? 0 : -1;
      if (panel && root.contains(panel)) {
        panel.classList.toggle("active", selected);
        panel.hidden = !selected;
      }
    });

    if (focusTab) selectedTab.focus();
  }

  function tabForHash(root, hash) {
    var tabs = Array.prototype.slice.call(root.querySelectorAll("[role='tab'][href^='#']"));
    if (!tabs.length) return null;
    return tabs.find(function (tab) { return "#" + tabId(tab) === hash; }) || tabs[0];
  }

  function initJobDetailTabs() {
    document.querySelectorAll("[data-tabs]").forEach(function (root) {
      var tabs = Array.prototype.slice.call(root.querySelectorAll("[role='tab'][href^='#']"));
      if (!tabs.length) return;

      root.classList.add("tabs-enhanced");
      activateTab(root, tabForHash(root, window.location.hash) || tabs[0], false);

      tabs.forEach(function (tab) {
        tab.addEventListener("click", function (event) {
          event.preventDefault();
          activateTab(root, tab, false);
          if (history.pushState) {
            history.pushState(null, "", tab.getAttribute("href"));
          } else {
            window.location.hash = tabId(tab);
          }
        });

        tab.addEventListener("keydown", function (event) {
          var index = tabs.indexOf(tab);
          var nextIndex = index;
          if (event.key === "ArrowRight" || event.key === "ArrowDown") nextIndex = (index + 1) % tabs.length;
          else if (event.key === "ArrowLeft" || event.key === "ArrowUp") nextIndex = (index + tabs.length - 1) % tabs.length;
          else if (event.key === "Home") nextIndex = 0;
          else if (event.key === "End") nextIndex = tabs.length - 1;
          else return;

          event.preventDefault();
          activateTab(root, tabs[nextIndex], true);
        });
      });
    });
  }

  function dashboardRows(table) {
    return Array.prototype.slice.call(table.querySelectorAll("tbody tr[data-dashboard-job-row='true']"));
  }

  function dashboardVisibleRows(root, sectionKey) {
    var table = root.querySelector("[data-dashboard-job-table='true'][data-dashboard-section-key='" + sectionKey + "']");
    if (!table) return 0;
    return dashboardRows(table).filter(function (row) { return !row.hidden; }).length;
  }

  function dashboardTotalRows(root, sectionKey) {
    var table = root.querySelector("[data-dashboard-job-table='true'][data-dashboard-section-key='" + sectionKey + "']");
    return table ? dashboardRows(table).length : 0;
  }

  function dashboardVisibleActiveJobIds(root) {
    var table = root.querySelector("[data-dashboard-job-table='true'][data-dashboard-section-key='active']");
    var visible = {};
    if (!table) return visible;

    dashboardRows(table).forEach(function (row) {
      var jobId = row.getAttribute("data-dashboard-job-id") || "";
      if (!row.hidden && jobId) visible[jobId] = true;
    });
    return visible;
  }

  function applyDashboardChartFilter(visibleActiveJobIds) {
    successRateChartEntries.forEach(function (entry) {
      entry.chart.data.datasets = entry.datasets.filter(function (dataset) {
        return Object.prototype.hasOwnProperty.call(visibleActiveJobIds, dataset.jobId || dataset.label);
      });
      entry.chart.update();
    });
  }

  function updateDashboardInactiveSummary(root, hasFilter) {
    var summary = root.querySelector("[data-dashboard-inactive-summary='true']");
    if (!summary) return;

    var originalText = summary.getAttribute("data-dashboard-original-text") || summary.textContent;
    if (!hasFilter) {
      summary.textContent = originalText;
      return;
    }

    var completedVisible = dashboardVisibleRows(root, "completed");
    var softDeletedVisible = dashboardVisibleRows(root, "soft-deleted");
    var completedTotal = dashboardTotalRows(root, "completed");
    var softDeletedTotal = dashboardTotalRows(root, "soft-deleted");
    summary.textContent = (completedVisible + softDeletedVisible) + " of " + (completedTotal + softDeletedTotal)
      + " job(s): " + completedVisible + " of " + completedTotal + " completed, "
      + softDeletedVisible + " of " + softDeletedTotal + " soft-deleted";
  }

  function initDashboardJobFilter() {
    var root = document.querySelector("[data-dashboard-filter-root='true']");
    var input = document.querySelector("[data-dashboard-filter-input='true']");
    if (!root || !input) return;

    var status = root.querySelector("[data-dashboard-filter-status='true']");

    function applyFilter() {
      var query = input.value.trim().toLowerCase();
      var hasFilter = query.length > 0;
      var visibleTotal = 0;
      var rowTotal = 0;

      root.querySelectorAll("[data-dashboard-job-table='true']").forEach(function (table) {
        var section = table.closest("[data-dashboard-job-section='true']");
        var count = section ? section.querySelector("[data-dashboard-section-count='true']") : null;
        var empty = section ? section.querySelector("[data-dashboard-filter-empty='true']") : null;
        var sectionVisible = 0;
        var rows = dashboardRows(table);

        rows.forEach(function (row) {
          var searchText = row.getAttribute("data-dashboard-search") || "";
          var visible = !hasFilter || searchText.indexOf(query) >= 0;
          row.hidden = !visible;
          rowTotal += 1;
          if (visible) {
            sectionVisible += 1;
            visibleTotal += 1;
          }
        });

        if (count) {
          var originalCount = Number(count.getAttribute("data-dashboard-original-count") || rows.length);
          count.textContent = hasFilter
            ? sectionVisible + " of " + originalCount + " job(s)"
            : originalCount + " job(s)";
        }

        if (empty) {
          empty.hidden = !hasFilter || sectionVisible > 0;
        }
      });

      updateDashboardInactiveSummary(root, hasFilter);
      applyDashboardChartFilter(dashboardVisibleActiveJobIds(root));
      if (status) {
        status.textContent = hasFilter
          ? "Showing " + visibleTotal + " of " + rowTotal + " job(s)."
          : "Showing " + rowTotal + " job(s).";
      }

      document.dispatchEvent(new CustomEvent("dashboard:filtered"));
    }

    input.addEventListener("input", applyFilter);
    applyFilter();
  }

  var dashboardColumnStorageKey = "ko-lite.dashboard.job-table.column-widths.v1";

  function dashboardResizableTables() {
    return Array.prototype.slice.call(document.querySelectorAll("table.job-table-dashboard[data-dashboard-resizable='true']"));
  }

  function readDashboardColumnWidths() {
    var stored;
    try {
      stored = window.localStorage.getItem(dashboardColumnStorageKey);
    } catch (error) {
      console.warn("Dashboard column widths could not be loaded.", error);
      return {};
    }

    if (!stored) return {};

    try {
      var parsed = JSON.parse(stored);
      return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
    } catch (error) {
      console.warn("Dashboard column widths could not be parsed.", error);
      return {};
    }
  }

  function saveDashboardColumnWidths(widths) {
    try {
      window.localStorage.setItem(dashboardColumnStorageKey, JSON.stringify(widths));
    } catch (error) {
      console.warn("Dashboard column widths could not be saved.", error);
    }
  }

  function dashboardColumnWidth(col, widths) {
    var key = col.getAttribute("data-column-key") || "";
    var width = Number(widths[key]);
    if (Number.isFinite(width) && width > 0) return width;
    return Number(col.getAttribute("data-default-width") || 0);
  }

  function dashboardAvailableTableWidth(table) {
    var wrap = table.closest(".table-wrap");
    if (wrap && wrap.clientWidth > 0) return wrap.clientWidth;

    var visibleWrap = dashboardResizableTables()
      .map(function (candidate) { return candidate.closest(".table-wrap"); })
      .filter(function (candidateWrap) { return candidateWrap && candidateWrap.clientWidth > 0; })[0];
    return visibleWrap ? visibleWrap.clientWidth : 0;
  }

  function applyDashboardColumnWidths(widths) {
    dashboardResizableTables().forEach(function (table) {
      var totalWidth = 0;
      table.querySelectorAll("col[data-column-key]").forEach(function (col) {
        var width = dashboardColumnWidth(col, widths);
        if (width > 0) {
          col.style.width = Math.round(width) + "px";
          totalWidth += width;
        }
      });

      if (totalWidth > 0) {
        var pixelWidth = Math.ceil(totalWidth) + "px";
        if (dashboardAvailableTableWidth(table) >= totalWidth) {
          table.style.width = "100%";
          table.style.minWidth = "0";
        } else {
          table.style.width = pixelWidth;
          table.style.minWidth = pixelWidth;
        }
      }
    });
  }

  function initDashboardColumnResize() {
    var tables = dashboardResizableTables();
    if (!tables.length) return;

    var widths = readDashboardColumnWidths();
    applyDashboardColumnWidths(widths);

    document.querySelectorAll("table.job-table-dashboard[data-dashboard-resizable='true'] .column-resize-handle").forEach(function (handle) {
      handle.addEventListener("pointerdown", function (event) {
        var table = handle.closest("table");
        var columnKey = handle.getAttribute("data-column-key") || "";
        var col = table ? table.querySelector("col[data-column-key='" + columnKey + "']") : null;
        if (!table || !col) return;

        event.preventDefault();
        var startX = event.clientX;
        var startWidth = dashboardColumnWidth(col, widths) || col.getBoundingClientRect().width;
        var minWidth = Number(col.getAttribute("data-min-width") || 72);
        document.body.classList.add("column-resizing");
        handle.setPointerCapture(event.pointerId);

        function resize(moveEvent) {
          var nextWidth = Math.max(minWidth, Math.round(startWidth + moveEvent.clientX - startX));
          widths[columnKey] = nextWidth;
          applyDashboardColumnWidths(widths);
        }

        function stopResize(stopEvent) {
          if (handle.hasPointerCapture && handle.hasPointerCapture(stopEvent.pointerId)) {
            handle.releasePointerCapture(stopEvent.pointerId);
          }

          handle.removeEventListener("pointermove", resize);
          handle.removeEventListener("pointerup", stopResize);
          handle.removeEventListener("pointercancel", stopResize);
          document.body.classList.remove("column-resizing");
          saveDashboardColumnWidths(widths);
        }

        handle.addEventListener("pointermove", resize);
        handle.addEventListener("pointerup", stopResize);
        handle.addEventListener("pointercancel", stopResize);
      });
    });
  }

  window.addEventListener("hashchange", function () {
    document.querySelectorAll("[data-tabs]").forEach(function (root) {
      var tab = tabForHash(root, window.location.hash);
      if (tab) activateTab(root, tab, false);
    });
  });

  document.addEventListener("mouseover", function (event) {
    var target = event.target;
    if (!(target instanceof HTMLElement)) return;
    var cell = target.closest("[data-slice-tooltip='true']");
    if (cell) showSliceTooltip(cell);
  });

  document.addEventListener("mouseout", function (event) {
    var target = event.target;
    if (!(target instanceof HTMLElement)) return;
    var cell = target.closest("[data-slice-tooltip='true']");
    if (cell && (!(event.relatedTarget instanceof Node) || !cell.contains(event.relatedTarget))) {
      hideSliceTooltip();
    }
  });

  document.addEventListener("focusin", function (event) {
    var target = event.target;
    if (target instanceof HTMLElement && target.matches("[data-slice-tooltip='true']")) {
      showSliceTooltip(target);
    }
  });

  document.addEventListener("focusout", function (event) {
    var target = event.target;
    if (target instanceof HTMLElement && target.matches("[data-slice-tooltip='true']")) {
      hideSliceTooltip();
    }
  });

  document.addEventListener("click", function (event) {
    var target = event.target;
    if (!(target instanceof HTMLElement)) return;

    var copyTarget = target.getAttribute("data-copy-target");
    if (copyTarget) {
      var input = document.querySelector("[name='" + copyTarget + "']");
      if (input && "value" in input && navigator.clipboard) {
        navigator.clipboard.writeText(input.value);
        target.textContent = "Copied";
        setTimeout(function () { target.textContent = "Copy JSON"; }, 1200);
      }
    }

    var confirmText = target.getAttribute("data-confirm-text");
    if (confirmText) {
      var form = target.closest("form");
      var confirmation = form ? form.querySelector("[name='confirmation']") : null;
      if (!confirmation || confirmation.value !== confirmText) {
        event.preventDefault();
        alert("Type exactly: " + confirmText);
      }
    }
  });

  function applyDashboardToggleResult(form, data) {
    var base = form.getAttribute("data-toggle-base") || "";
    var enabled = data.enabled === true;
    var versionInput = form.querySelector("input[name='expectedVersion']");
    if (versionInput && data.version !== null && typeof data.version !== "undefined") {
      versionInput.value = data.version;
    }

    if (base) {
      form.setAttribute("action", base + (enabled ? "/disable" : "/enable"));
    }

    var button = form.querySelector("button[type='submit']");
    if (button) {
      button.textContent = enabled ? "Pause" : "Resume";
    }

    var row = form.closest("tr");
    if (row) {
      if (typeof data.statusText === "string" && data.statusText) {
        var badge = row.querySelector(".status-cell .badge");
        if (badge) {
          badge.textContent = data.statusText;
          badge.className = "badge " + (data.statusCss || "badge-neutral");
        }
      }

      if (typeof data.nextText === "string") {
        var nextCell = row.querySelector(".next-cell");
        if (nextCell) {
          nextCell.textContent = data.nextText;
          nextCell.setAttribute("title", data.nextDetail || "");
        }
      }
    }
  }

  function submitDashboardToggle(form) {
    if (form.getAttribute("data-toggle-inflight") === "true") return;

    var token = form.querySelector("input[name='__RequestVerificationToken']");
    var button = form.querySelector("button[type='submit']");
    if (!token) {
      form.submit();
      return;
    }

    form.setAttribute("data-toggle-inflight", "true");
    if (button) button.disabled = true;

    fetch(form.action, {
      method: "POST",
      headers: {
        "X-Requested-With": "XMLHttpRequest",
        "X-CSRF-TOKEN": token.value,
        "Accept": "application/json"
      },
      body: new URLSearchParams(new FormData(form))
    }).then(function (response) {
      return response.json().then(function (data) {
        return { ok: response.ok, status: response.status, data: data };
      }).catch(function () {
        return { ok: response.ok, status: response.status, data: null };
      });
    }).then(function (result) {
      if (result.data) {
        // A 409 conflict still returns the current projected state so the row resyncs.
        applyDashboardToggleResult(form, result.data);
      }
      if (!result.ok) {
        var jobId = form.getAttribute("data-dashboard-job-id") || "this job";
        var message = result.data && result.data.error ? result.data.error : ("Request failed (" + result.status + ").");
        window.alert("Could not update " + jobId + ". " + message);
      }
    }).catch(function () {
      var jobId = form.getAttribute("data-dashboard-job-id") || "this job";
      window.alert("Could not update " + jobId + ". Refresh the page and try again.");
    }).then(function () {
      form.removeAttribute("data-toggle-inflight");
      if (button) button.disabled = false;
    });
  }

  function initDashboardJobToggle() {
    document.addEventListener("submit", function (event) {
      var form = event.target;
      if (!(form instanceof HTMLFormElement)) return;
      if (form.getAttribute("data-dashboard-toggle") !== "true") return;
      event.preventDefault();
      submitDashboardToggle(form);
    });
  }

  function bulkCheckboxes(root) {
    return Array.prototype.slice.call(root.querySelectorAll("[data-bulk-select]"));
  }

  function bulkSelectedRows(root) {
    return bulkCheckboxes(root).filter(function (checkbox) {
      var row = checkbox.closest("tr");
      return checkbox.checked && (!row || !row.hidden);
    });
  }

  function initBulkSelect() {
    var root = document.querySelector("[data-dashboard-filter-root='true']");
    var bar = document.querySelector("[data-bulk-bar]");
    if (!root || !bar) return;

    var count = bar.querySelector("[data-bulk-count]");
    var inputs = bar.querySelector("[data-bulk-inputs]");
    var form = bar.querySelector("form");
    var clear = bar.querySelector("[data-bulk-clear]");

    function refresh() {
      var selected = bulkSelectedRows(root);
      bar.hidden = selected.length === 0;
      if (count) count.textContent = selected.length + " selected";

      root.querySelectorAll("[data-bulk-select-all]").forEach(function (master) {
        var section = master.getAttribute("data-bulk-section");
        var table = root.querySelector("[data-dashboard-job-table][data-dashboard-section-key='" + section + "']");
        if (!table) return;
        var rows = bulkCheckboxes(table).filter(function (checkbox) {
          var row = checkbox.closest("tr");
          return !row || !row.hidden;
        });
        var checkedRows = rows.filter(function (checkbox) { return checkbox.checked; });
        master.checked = rows.length > 0 && checkedRows.length === rows.length;
        master.indeterminate = checkedRows.length > 0 && checkedRows.length < rows.length;
      });
    }

    root.addEventListener("change", function (event) {
      var target = event.target;
      if (!(target instanceof HTMLInputElement)) return;

      if (target.hasAttribute("data-bulk-select-all")) {
        var section = target.getAttribute("data-bulk-section");
        var table = root.querySelector("[data-dashboard-job-table][data-dashboard-section-key='" + section + "']");
        if (table) {
          bulkCheckboxes(table).forEach(function (checkbox) {
            var row = checkbox.closest("tr");
            if (!row || !row.hidden) checkbox.checked = target.checked;
          });
        }
        refresh();
        return;
      }

      if (target.hasAttribute("data-bulk-select")) {
        refresh();
      }
    });

    if (clear) {
      clear.addEventListener("click", function () {
        bulkCheckboxes(root).forEach(function (checkbox) { checkbox.checked = false; });
        refresh();
      });
    }

    document.addEventListener("dashboard:filtered", function () {
      bulkCheckboxes(root).forEach(function (checkbox) {
        var row = checkbox.closest("tr");
        if (row && row.hidden) checkbox.checked = false;
      });
      refresh();
    });

    if (form && inputs) {
      form.addEventListener("submit", function (event) {
        var selected = bulkSelectedRows(root);
        if (selected.length === 0) {
          event.preventDefault();
          return;
        }

        inputs.textContent = "";
        selected.forEach(function (checkbox) {
          var jobInput = document.createElement("input");
          jobInput.type = "hidden";
          jobInput.name = "jobIds";
          jobInput.value = checkbox.value;
          inputs.appendChild(jobInput);

          var versionInput = document.createElement("input");
          versionInput.type = "hidden";
          versionInput.name = "expectedVersions";
          versionInput.value = checkbox.getAttribute("data-expected-version") || "0";
          inputs.appendChild(versionInput);
        });
      });
    }

    refresh();
  }

  window.BuildSuccessRateChart = buildSuccessRateChart;
  window.BuildJobDetailChart = buildJobDetailChart;
  window.initJobDetailTabs = initJobDetailTabs;
  window.toggleSuccessRateSeries = toggleSuccessRateSeries;
  window.initDashboardJobFilter = initDashboardJobFilter;
  window.initDashboardColumnResize = initDashboardColumnResize;
  window.initDashboardJobToggle = initDashboardJobToggle;
  window.initBulkSelect = initBulkSelect;
  initSuccessRateCharts();
  initJobDetailCharts();
  initJobDetailTabs();
  initDashboardJobFilter();
  initDashboardColumnResize();
  initDashboardJobToggle();
  initBulkSelect();
})();
