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
        jobId: series.jobId || series.name,
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

  function buildThrottleSeverityChart(canvas, payload) {
    if (!window.Chart || !canvas || !payload || !payload.points) return null;

    var rangeStart = Date.parse(payload.rangeStartUtc);
    var rangeEnd = Date.parse(payload.rangeEndUtc);
    var rangeMs = Math.max(0, rangeEnd - rangeStart);
    var dataset = {
      label: "Throttled attempts",
      data: payload.points.map(function (point) {
        return {
          x: point.x,
          y: point.y,
          throttled: point.throttled,
          total: point.total,
          bucket: point.bucket,
          label: point.label,
          percentText: point.percentText
        };
      }),
      borderColor: "#cf222e",
      backgroundColor: "rgba(207, 34, 46, 0.12)",
      borderWidth: 2.25,
      fill: true,
      pointBorderColor: "#fff",
      pointBorderWidth: 1.25,
      pointHitRadius: 10,
      pointHoverRadius: 10,
      pointRadius: function (context) {
        return context.raw && context.raw.y !== null && typeof context.raw.y !== "undefined" ? 4.5 : 0;
      },
      tension: 0.22,
      spanGaps: false
    };

    return new Chart(canvas, {
      type: "line",
      data: { datasets: [dataset] },
      options: {
        animation: false,
        maintainAspectRatio: false,
        normalized: true,
        parsing: false,
        interaction: { intersect: false, mode: "nearest" },
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              title: function (items) {
                var raw = items.length ? items[0].raw : null;
                return raw ? raw.bucket : "";
              },
              label: function (context) {
                var raw = context.raw || {};
                var value = raw.y === null || typeof raw.y === "undefined" ? "n/a" : raw.percentText;
                var count = typeof raw.throttled === "number" && typeof raw.total === "number"
                  ? " (" + raw.throttled + "/" + raw.total + ")"
                  : "";
                return "Throttled: " + value + count;
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
            suggestedMax: 100,
            grid: { color: "rgba(208, 215, 222, 0.75)" },
            ticks: {
              color: "#57606a",
              callback: function (value) { return value + "%"; }
            },
            title: { display: true, text: "Throttled attempts", color: "#57606a" }
          }
        }
      }
    });
  }

  function initThrottleSeverityCharts() {
    document.querySelectorAll("[data-chartjs-throttle]").forEach(function (container) {
      var chartId = container.getAttribute("data-chartjs-throttle");
      var canvas = document.getElementById(chartId);
      var payloadNode = document.getElementById(chartId + "-data");
      if (!canvas || !payloadNode) return;

      try {
        var payload = JSON.parse(payloadNode.textContent || "{}");
        buildThrottleSeverityChart(canvas, payload);
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

  function buildSlicesProcessedChart(canvas, payload) {
    if (!window.Chart || !canvas || !payload || !payload.points) return null;

    var rangeStart = Date.parse(payload.rangeStartUtc);
    var rangeEnd = Date.parse(payload.rangeEndUtc);
    var rangeMs = Math.max(0, rangeEnd - rangeStart);

    function seriesData(key) {
      return payload.points.map(function (point) {
        return {
          x: point.x,
          y: point[key],
          succeeded: point.succeeded,
          failed: point.failed,
          total: point.total,
          bucket: point.bucket,
          label: point.label
        };
      });
    }

    function lineDataset(label, key, color) {
      return {
        label: label,
        data: seriesData(key),
        borderColor: color,
        backgroundColor: color,
        borderWidth: 2,
        pointBorderColor: "#fff",
        pointBorderWidth: 1.25,
        pointHitRadius: 10,
        pointHoverRadius: 10,
        pointRadius: function (context) {
          var raw = context.raw || {};
          return raw.y !== null && typeof raw.y !== "undefined" && raw.y !== 0 ? 5 : 0;
        },
        tension: 0,
        spanGaps: false
      };
    }

    var datasets = [
      lineDataset("Succeeded", "succeeded", "#1a7f37"),
      lineDataset("Failed / dead-lettered", "failed", "#cf222e")
    ];

    return new Chart(canvas, {
      type: "line",
      data: { datasets: datasets },
      options: {
        animation: false,
        maintainAspectRatio: false,
        normalized: true,
        parsing: false,
        interaction: { intersect: false, mode: "index" },
        plugins: {
          legend: {
            display: true,
            position: "bottom",
            labels: { boxWidth: 28, color: "#24292f", font: { size: 12 }, usePointStyle: true }
          },
          tooltip: {
            callbacks: {
              title: function (items) {
                var raw = items.length ? items[0].raw : null;
                return raw ? raw.bucket : "";
              },
              label: function (context) {
                var raw = context.raw || {};
                return context.dataset.label + ": " + (raw.y || 0) + " slice(s)";
              },
              footer: function (items) {
                var raw = items.length ? items[0].raw : null;
                return raw ? "Total: " + (raw.total || 0) : "";
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
            ticks: { color: "#57606a", precision: 0 },
            title: { display: true, text: "Slices processed", color: "#57606a" }
          }
        }
      }
    });
  }

  function initSlicesProcessedCharts() {
    document.querySelectorAll("[data-chartjs-activity]").forEach(function (container) {
      var chartId = container.getAttribute("data-chartjs-activity");
      var canvas = document.getElementById(chartId);
      var payloadNode = document.getElementById(chartId + "-data");
      if (!canvas || !payloadNode) return;

      try {
        var payload = JSON.parse(payloadNode.textContent || "{}");
        buildSlicesProcessedChart(canvas, payload);
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
    var selectedPanel = null;
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
        if (selected) selectedPanel = panel;
      }
    });

    if (focusTab) selectedTab.focus();

    // A dependency graph deferred while its tab was hidden must be rendered now that its panel is
    // visible: Cytoscape computes degenerate geometry (collapsed nodes, zero-length invisible
    // edges) when built in a 0x0 display:none container, and a later resize/fit does not recover.
    if (selectedPanel) renderPendingDependencyGraphs(selectedPanel);
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
      if (typeof data.primaryKey === "string" && data.primaryKey) {
        renderStatusPill(row.querySelector(".status-cell [data-status-pill]"), data);
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

  // Rebuilds a color-only status pill from a toggle response so the health half's color/tooltip and
  // the completeness half stay consistent after pause/resume. The pill carries no text or dot.
  function renderStatusPill(pill, data) {
    if (!pill) return;

    var health = pill.querySelector(".status-seg-health");
    if (health) {
      health.className = "status-seg status-seg-health status-" + (data.primaryKey || "healthy");
      if (typeof data.healthTooltip === "string") {
        health.setAttribute("title", data.healthTooltip);
        health.setAttribute("aria-label", data.healthTooltip);
      }
    }

    var completeness = pill.querySelector(".status-seg-completeness");
    if (data.showCompleteness) {
      pill.classList.remove("status-pill-solid");
      if (!completeness) {
        completeness = document.createElement("span");
        completeness.setAttribute("role", "img");
        pill.appendChild(completeness);
      }
      completeness.className = "status-seg status-seg-completeness " + (data.completenessCss || "status-complete");
      completeness.setAttribute("title", data.completenessTooltip || "");
      completeness.setAttribute("aria-label", data.completenessTooltip || "");
    } else if (completeness) {
      completeness.remove();
      pill.classList.add("status-pill-solid");
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

    var dependencies = bar.querySelector("[data-bulk-dependencies]");
    if (dependencies) {
      dependencies.addEventListener("click", function () {
        var selected = bulkSelectedRows(root);
        if (selected.length === 0) return;
        var query = selected
          .map(function (checkbox) { return "job=" + encodeURIComponent(checkbox.value); })
          .join("&");
        window.location.href = "/dependencies?" + query;
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
  function initUpdateBadge() {
    var badge = document.querySelector(".update-badge");
    if (!badge) return;
    var button = badge.querySelector(".update-badge-button");
    if (!button) return;

    function close() {
      badge.classList.remove("is-open");
      button.setAttribute("aria-expanded", "false");
    }

    button.addEventListener("click", function (event) {
      event.stopPropagation();
      var open = badge.classList.toggle("is-open");
      button.setAttribute("aria-expanded", open ? "true" : "false");
    });

    document.addEventListener("click", function (event) {
      if (!badge.contains(event.target)) close();
    });

    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape") close();
    });
  }

  function dependencyChipValues(chips) {
    return Array.prototype.map.call(chips.querySelectorAll("[data-dependency-chip]"), function (chip) {
      return chip.getAttribute("data-dep-value");
    });
  }

  function makeDependencyChip(value, label) {
    var chip = document.createElement("span");
    chip.className = "dep-chip";
    chip.setAttribute("data-dependency-chip", "");
    chip.setAttribute("data-dep-value", value);

    var labelNode = document.createElement("span");
    labelNode.className = "dep-chip-label";
    labelNode.title = value;
    labelNode.textContent = label;
    chip.appendChild(labelNode);

    var remove = document.createElement("button");
    remove.type = "button";
    remove.className = "dep-chip-remove";
    remove.setAttribute("data-dependency-remove", "");
    remove.setAttribute("aria-label", "Remove dependency " + label);
    remove.innerHTML = "&times;";
    chip.appendChild(remove);

    return chip;
  }

  function syncDependencyHidden(picker) {
    var chips = picker.querySelector("[data-dependency-chips]");
    var hidden = picker.querySelector("[data-dependency-value]");
    if (!chips || !hidden) return;
    hidden.value = dependencyChipValues(chips).join("\n");
  }

  function initDependencyPickers() {
    document.querySelectorAll("[data-dependency-picker]").forEach(function (picker) {
      var select = picker.querySelector("[data-dependency-select]");
      var addButton = picker.querySelector("[data-dependency-add]");
      var chips = picker.querySelector("[data-dependency-chips]");
      var hidden = picker.querySelector("[data-dependency-value]");
      if (!chips || !hidden) return;

      // Reconcile the hidden field with the server-rendered chips on load.
      syncDependencyHidden(picker);

      if (select && addButton) {
        addButton.addEventListener("click", function () {
          var value = select.value;
          if (!value) return;
          if (dependencyChipValues(chips).indexOf(value) !== -1) {
            select.value = "";
            return;
          }

          var option = select.options[select.selectedIndex];
          var label = option ? option.textContent : value;
          chips.appendChild(makeDependencyChip(value, label));
          syncDependencyHidden(picker);
          select.value = "";
        });
      }

      chips.addEventListener("click", function (event) {
        var target = event.target;
        if (!(target instanceof HTMLElement)) return;
        var remove = target.closest("[data-dependency-remove]");
        if (!remove) return;
        var chip = remove.closest("[data-dependency-chip]");
        if (chip && chip.parentNode) {
          chip.parentNode.removeChild(chip);
          syncDependencyHidden(picker);
        }
      });
    });
  }

  function friendlyDepGraphKind(kind) {
    switch (kind) {
      case "Job": return "Job";
      case "KustoFunction": return "Function";
      case "KustoMaterializedView": return "Materialized view";
      case "KustoTable": return "Table";
      case "KustoExternal": return "External entity";
      default: return "Entity";
    }
  }

  function inspectorCountRow(label, value, isTotal) {
    var row = tooltipRow(label, value);
    if (isTotal) row.classList.add("is-total");
    return row;
  }

  // Renders the docked details-panel content for a node: title, kind, status dot + text,
  // slice counts (jobs only), and an open-job link when available.
  function buildDepGraphInspector(panel, node) {
    panel.textContent = "";

    var title = document.createElement("div");
    title.className = "dependency-graph-inspector-title";
    title.textContent = node.label;
    panel.appendChild(title);

    var kind = document.createElement("div");
    kind.className = "dependency-graph-inspector-kind";
    kind.textContent = friendlyDepGraphKind(node.kind);
    panel.appendChild(kind);

    var meta = document.createElement("div");
    meta.className = "dependency-graph-inspector-meta";
    var swatch = document.createElement("span");
    swatch.className = "dependency-graph-swatch status-" + node.statusKey;
    swatch.setAttribute("aria-hidden", "true");
    meta.appendChild(swatch);
    var statusText = document.createElement("span");
    statusText.textContent = node.statusText;
    meta.appendChild(statusText);
    panel.appendChild(meta);

    if (node.counts) {
      var counts = node.counts;
      var countsEl = document.createElement("div");
      countsEl.className = "dependency-graph-inspector-counts";
      [
        ["Completed", counts.completed],
        ["Running", counts.running],
        ["Queued", counts.queued],
        ["Failed", counts.failed],
        ["Dead-lettered", counts.deadLettered],
        ["Blocked", counts.dependencyBlocked],
        ["Missing", counts.missing]
      ].forEach(function (row) {
        if (row[1] > 0) countsEl.appendChild(inspectorCountRow(row[0], String(row[1]), false));
      });
      countsEl.appendChild(inspectorCountRow("Total", String(counts.total), true));
      panel.appendChild(countsEl);
    }

    if (node.resolved && node.href) {
      var link = document.createElement("a");
      link.className = "dependency-graph-inspector-link";
      link.href = node.href;
      link.textContent = "Open job details";
      panel.appendChild(link);
    }
  }

  function buildDepGraphInspectorPlaceholder(panel) {
    panel.textContent = "";
    var hint = document.createElement("p");
    hint.className = "dependency-graph-inspector-placeholder";
    hint.textContent = "Hover or focus a node to see its status and slice counts.";
    panel.appendChild(hint);
  }

  // Controller for the docked details panel: show(node) on hover/focus, rest() returns to the
  // single focal node (or a hint). No-ops safely when the panel element is absent.
  function createDepGraphInspector(container, nodes) {
    var panel = container.querySelector("[data-dependency-graph-inspector]");
    var focalNodes = (nodes || []).filter(function (node) { return node.focal; });
    var restNode = focalNodes.length === 1 ? focalNodes[0] : null;
    function rest() {
      if (!panel) return;
      if (restNode) buildDepGraphInspector(panel, restNode);
      else buildDepGraphInspectorPlaceholder(panel);
    }
    return {
      show: function (node) { if (panel && node) buildDepGraphInspector(panel, node); },
      rest: rest
    };
  }

  function renderDependencyLegend(container, legend, hasImplicit) {
    var legendEl = container.querySelector("[data-dependency-graph-legend]");
    if (!legendEl) return;
    while (legendEl.firstChild) legendEl.removeChild(legendEl.firstChild);
    (legend || []).forEach(function (item) {
      var entry = document.createElement("span");
      entry.className = "dependency-graph-legend-item";
      var swatch = document.createElement("span");
      swatch.className = "dependency-graph-swatch status-" + item.statusKey;
      swatch.setAttribute("aria-hidden", "true");
      entry.appendChild(swatch);
      entry.appendChild(document.createTextNode(item.label));
      legendEl.appendChild(entry);
    });
    if (hasImplicit) {
      var implicitEntry = document.createElement("span");
      implicitEntry.className = "dependency-graph-legend-item";
      var line = document.createElement("span");
      line.className = "dependency-graph-legend-line is-implicit";
      line.setAttribute("aria-hidden", "true");
      implicitEntry.appendChild(line);
      implicitEntry.appendChild(document.createTextNode("Implicit dependency (undeclared)"));
      legendEl.appendChild(implicitEntry);
    }
  }

  var depGraphDagreRegistered = false;

  function ensureDepGraphDagre() {
    if (depGraphDagreRegistered) return true;
    if (!window.cytoscape || !window.cytoscapeDagre) return false;
    try {
      window.cytoscape.use(window.cytoscapeDagre);
    } catch (error) {
      // use() throws if the extension is already registered; treat that as success.
    }
    depGraphDagreRegistered = true;
    return true;
  }

  // Cytoscape stylesheet mirroring the legend palette: node fill/border per status + Kusto kind
  // (dashed for soft-deleted/unknown/external), focal nodes get a thicker border, implicit
  // (undeclared) edges are dashed amber, and ".faded" dims everything outside a hovered node's
  // neighborhood. Kept in JS because Cytoscape styles its canvas from this array, not from CSS.
  function depGraphStylesheet() {
    var STATUS_COLORS = {
      healthy: ["#dafbe1", "#1a7f37"],
      borderline: ["#fff8c5", "#9a6700"],
      attention: ["#ffebe9", "#cf222e"],
      waitingonupstream: ["#dafbe1", "#1a7f37"],
      running: ["#ddf4ff", "#0969da"],
      dependencyblocked: ["#fff8c5", "#9a6700"],
      paused: ["#f6f8fa", "#8c959f"],
      failed: ["#ffebe9", "#cf222e"],
      completed: ["#f6f8fa", "#8c959f"],
      softdeleted: ["#f6f8fa", "#cf222e"],
      unknown: ["#f6f8fa", "#8c959f"],
      kustofunction: ["#fbefff", "#8250df"],
      kustomaterializedview: ["#e8f6f8", "#1b7c83"],
      kustotable: ["#eef1f4", "#57606a"],
      kustoexternal: ["#f6f8fa", "#8c959f"]
    };
    var DASHED = { softdeleted: true, unknown: true, kustoexternal: true };
    var QUIETER = { kustofunction: true, kustomaterializedview: true, kustotable: true, kustoexternal: true };

    var style = [
      {
        selector: "node",
        style: {
          "shape": "round-rectangle",
          "background-color": "#fff",
          "border-color": "#d0d7de",
          "border-width": 1.5,
          "label": "data(label)",
          "color": "#1f2328",
          "font-size": 13,
          "font-weight": 600,
          "text-wrap": "wrap",
          "text-max-width": "176px",
          "text-valign": "center",
          "text-halign": "center",
          "width": "label",
          "height": "label",
          "padding": "10px"
        }
      },
      {
        selector: "edge",
        style: {
          "width": 1.5,
          "line-color": "#afb8c1",
          "target-arrow-color": "#afb8c1",
          "target-arrow-shape": "triangle",
          "arrow-scale": 0.9,
          "curve-style": "bezier"
        }
      },
      {
        selector: "edge.is-implicit",
        style: { "line-color": "#9a6700", "target-arrow-color": "#9a6700", "line-style": "dashed" }
      },
      { selector: "node.is-focal", style: { "border-width": 3 } },
      { selector: ".faded", style: { "opacity": 0.2 } }
    ];

    Object.keys(STATUS_COLORS).forEach(function (key) {
      var pair = STATUS_COLORS[key];
      var nodeStyle = { "background-color": pair[0], "border-color": pair[1] };
      if (DASHED[key]) nodeStyle["border-style"] = "dashed";
      if (QUIETER[key]) nodeStyle["font-weight"] = 500;
      style.push({ selector: "node.status-" + key, style: nodeStyle });
    });

    return style;
  }

  function drawDependencyGraph(container, data) {
    var viewport = container.querySelector("[data-dependency-graph-viewport]");
    if (!viewport || !data || !data.nodes || !data.nodes.length) return;
    if (!window.cytoscape) return;

    var nodeIds = {};
    data.nodes.forEach(function (node) { nodeIds[node.id] = true; });

    var elements = [];
    data.nodes.forEach(function (node) {
      elements.push({
        data: {
          id: node.id,
          label: node.label,
          statusKey: node.statusKey,
          statusText: node.statusText,
          kind: node.kind,
          counts: node.counts,
          href: node.href,
          resolved: node.resolved,
          focal: node.focal
        },
        classes: "status-" + node.statusKey +
          " kind-" + (node.kind ? node.kind.toLowerCase() : "job") +
          (node.focal ? " is-focal" : "")
      });
    });
    var edgeSeq = 0;
    (data.edges || []).forEach(function (edge) {
      if (!nodeIds[edge.from] || !nodeIds[edge.to]) return;
      elements.push({
        data: { id: "e" + (edgeSeq++), source: edge.from, target: edge.to },
        classes: edge.implicit ? "is-implicit" : ""
      });
    });

    if (container.__depGraphCy) {
      container.__depGraphCy.destroy();
      container.__depGraphCy = null;
    }

    var useDagre = ensureDepGraphDagre();
    var cy = window.cytoscape({
      container: viewport,
      elements: elements,
      style: depGraphStylesheet(),
      minZoom: 0.1,
      maxZoom: 2.5,
      wheelSensitivity: 0.2,
      boxSelectionEnabled: false,
      autounselectify: true
    });
    container.__depGraphCy = cy;

    var inspector = createDepGraphInspector(container, data.nodes);

    // Hover focus: dim everything outside the hovered node's neighborhood, and drive the panel.
    cy.on("mouseover", "node", function (evt) {
      var node = evt.target;
      cy.elements().difference(node.closedNeighborhood()).addClass("faded");
      if (inspector) inspector.show(node.data());
      viewport.style.cursor = node.data("href") ? "pointer" : "default";
    });
    cy.on("mouseout", "node", function () {
      cy.elements().removeClass("faded");
      if (inspector) inspector.rest();
      viewport.style.cursor = "default";
    });
    // Single tap inspects/pans; double-tap (or the panel's "Open job details" link) navigates.
    cy.on("dbltap", "node", function (evt) {
      var href = evt.target.data("href");
      if (href) window.location.href = href;
    });

    var layout = cy.layout(useDagre
      ? { name: "dagre", rankDir: "TB", nodeSep: 36, rankSep: 56, edgeSep: 10, padding: 24 }
      : { name: "breadthfirst", directed: true, padding: 24 });
    layout.one("layoutstop", function () { cy.fit(undefined, 24); });
    layout.run();

    container.classList.add("is-rendered");
    renderDependencyLegend(container, data.legend, (data.edges || []).some(function (edge) { return edge.implicit; }));
    if (inspector) inspector.rest();
  }

  function renderDependencyGraph(container) {
    var dataNode = container.querySelector("[data-dependency-graph-data]");
    if (!dataNode) return;
    var data;
    try {
      data = JSON.parse(dataNode.textContent);
    } catch (error) {
      return;
    }
    drawDependencyGraph(container, data);
  }

  function resolveKustoConsumers(figure, button, statusEl) {
    var focalAttr = figure.getAttribute("data-dependency-graph-focal") || "";
    var jobIds = focalAttr.split(/\s+/).filter(function (id) { return id.length; });
    if (!jobIds.length) return;

    button.disabled = true;
    if (statusEl) {
      statusEl.classList.remove("is-error");
      statusEl.textContent = "Resolving Kusto lineage\u2026";
    }

    fetch("/api/dependency-graph/kusto-consumers", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ jobIds: jobIds })
    }).then(function (response) {
      return response.json().then(function (body) { return { ok: response.ok, body: body }; });
    }).then(function (result) {
      if (!result.ok || !result.body || result.body.error) {
        throw new Error((result.body && result.body.error) || "Failed to resolve Kusto lineage.");
      }
      drawDependencyGraph(figure, result.body);
      var kustoCount = (result.body.nodes || []).filter(function (node) {
        return node.kind && node.kind.indexOf("Kusto") === 0;
      }).length;
      var implicitCount = (result.body.edges || []).filter(function (edge) { return edge.implicit; }).length;
      if (statusEl) {
        statusEl.classList.remove("is-error");
        if (kustoCount || implicitCount) {
          statusEl.textContent = "Resolved " + kustoCount + " Kusto entity(ies)" +
            (implicitCount ? (" and " + implicitCount + " implicit dependency(ies)") : "") + ".";
        } else {
          statusEl.textContent = "No Kusto lineage found.";
        }
      }
      button.disabled = false;
    }).catch(function (error) {
      if (statusEl) {
        statusEl.classList.add("is-error");
        statusEl.textContent = error && error.message ? error.message : "Failed to resolve Kusto lineage.";
      }
      button.disabled = false;
    });
  }

  function wireDepGraphZoom(figure) {
    function cy() { return figure.__depGraphCy; }
    function zoomBy(factor) {
      var c = cy();
      if (!c) return;
      c.zoom({ level: c.zoom() * factor, renderedPosition: { x: c.width() / 2, y: c.height() / 2 } });
    }
    function bind(selector, handler) {
      var el = figure.querySelector(selector);
      if (el) el.addEventListener("click", handler);
    }
    bind("[data-dependency-graph-zoom-in]", function () { zoomBy(1.2); });
    bind("[data-dependency-graph-zoom-out]", function () { zoomBy(1 / 1.2); });
    bind("[data-dependency-graph-fit]", function () { var c = cy(); if (c) c.fit(undefined, 24); });
    bind("[data-dependency-graph-reset]", function () { var c = cy(); if (c) { c.zoom(1); c.center(); } });
  }

  // Renders a dependency graph only when its container is actually visible. Building Cytoscape in a
  // hidden (display:none) tab panel yields a 0x0 canvas, which collapses node geometry and makes
  // every edge a zero-size (invisible) line; a later resize/fit/re-layout does not recover it, so
  // hidden graphs are marked pending and rendered on first visibility (see activateTab) instead.
  function renderDependencyGraphIfVisible(figure) {
    if (!figure || figure.__depGraphCy) return;
    var viewport = figure.querySelector("[data-dependency-graph-viewport]");
    if (viewport && viewport.clientWidth > 0 && viewport.clientHeight > 0) {
      figure.__depGraphPending = false;
      renderDependencyGraph(figure);
    } else {
      figure.__depGraphPending = true;
    }
  }

  function renderPendingDependencyGraphs(root) {
    (root || document).querySelectorAll("[data-dependency-graph]").forEach(function (figure) {
      if (figure.__depGraphPending && !figure.__depGraphCy) renderDependencyGraphIfVisible(figure);
    });
  }

  function initDependencyGraphs() {
    document.querySelectorAll("[data-dependency-graph]").forEach(function (figure) {
      wireDepGraphZoom(figure);
      var button = figure.querySelector("[data-dependency-graph-resolve]");
      var statusEl = figure.querySelector("[data-dependency-graph-status]");
      if (button) {
        button.addEventListener("click", function () { resolveKustoConsumers(figure, button, statusEl); });
      }
      renderDependencyGraphIfVisible(figure);
    });
  }

  window.initDependencyGraphs = initDependencyGraphs;
  window.initDependencyPickers = initDependencyPickers;
  window.initBulkSelect = initBulkSelect;
  initSuccessRateCharts();
  initThrottleSeverityCharts();
  initSlicesProcessedCharts();
  initJobDetailCharts();
  initJobDetailTabs();
  initDashboardJobFilter();
  initDashboardColumnResize();
  initDashboardJobToggle();
  initBulkSelect();
  initUpdateBadge();
  initDependencyPickers();
  initDependencyGraphs();
})();
