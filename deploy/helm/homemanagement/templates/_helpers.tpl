{{/*
Common labels applied to all resources.
*/}}
{{- define "homemanagement.labels" -}}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: homemanagement
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}

{{/*
Selector labels for a given component.
*/}}
{{- define "homemanagement.selectorLabels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .release }}
{{- end -}}

{{/*
Resolve image reference: registry/repository:tag
*/}}
{{- define "homemanagement.image" -}}
{{ .global.imageRegistry }}/{{ .image.repository }}:{{ .image.tag | default .global.imageTag }}
{{- end -}}

{{/*
Generate Docker config JSON for imagePullSecret.
*/}}
{{- define "homemanagement.dockerconfigjson" -}}
{{- $registry := .Values.global.imagePullSecret.registry -}}
{{- $username := .Values.global.imagePullSecret.username -}}
{{- $password := .Values.global.imagePullSecret.password -}}
{{- $auth := printf "%s:%s" $username $password | b64enc -}}
{{- printf "{\"auths\":{\"%s\":{\"username\":\"%s\",\"password\":\"%s\",\"auth\":\"%s\"}}}" $registry $username $password $auth -}}
{{- end -}}

{{- define "homemanagement.controlScheme" -}}
{{- if .Values.managedServices.enabled -}}https{{- else -}}http{{- end -}}
{{- end -}}

{{- define "homemanagement.controlTlsEnv" -}}
{{- if .Values.managedServices.enabled }}
- name: Kestrel__Certificates__Default__Path
  value: "/control-tls/tls.crt"
- name: Kestrel__Certificates__Default__KeyPath
  value: "/control-tls/tls.key"
- name: SSL_CERT_FILE
  value: "/control-trust/ca-bundle.pem"
{{- end -}}
{{- end -}}

{{- define "homemanagement.controlTlsMounts" -}}
{{- if .Values.managedServices.enabled }}
- name: control-tls
  mountPath: /control-tls
  readOnly: true
- name: control-trust
  mountPath: /control-trust
  readOnly: true
{{- end -}}
{{- end -}}

{{- define "homemanagement.controlTlsVolumes" -}}
{{- if .Values.managedServices.enabled }}
- name: control-tls
  secret:
    secretName: {{ required "managedServices.tlsSecretName is required" .Values.managedServices.tlsSecretName }}
- name: control-trust
  configMap:
    name: {{ required "managedServices.trustBundleConfigMap is required" .Values.managedServices.trustBundleConfigMap }}
{{- end -}}
{{- end -}}

{{- define "homemanagement.controlIngressTls" -}}
{{- if .Values.managedServices.enabled }}
nginx.ingress.kubernetes.io/backend-protocol: "HTTPS"
nginx.ingress.kubernetes.io/proxy-ssl-verify: "on"
nginx.ingress.kubernetes.io/proxy-ssl-server-name: "on"
nginx.ingress.kubernetes.io/proxy-ssl-name: "hm-control"
nginx.ingress.kubernetes.io/proxy-ssl-secret: "{{ .Values.namespace }}/{{ .Values.managedServices.tlsSecretName }}"
{{- end -}}
{{- end -}}
