variable "project_name" {
  type    = string
  default = "garage-management"
}

variable "environment" {
  type    = string
  default = "homologacao"
}

variable "region" {
  type    = string
  default = "us-east-1"
}

variable "cluster_name" {
  type = string
}

variable "container_image" {
  type    = string
  default = "ghcr.io/kmlyteixeira/garage-management-os-service:latest"
}

variable "replicas" {
  type    = number
  default = 2
}
